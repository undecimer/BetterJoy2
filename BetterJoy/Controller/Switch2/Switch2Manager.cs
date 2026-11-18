using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterJoy.Forms;
using BetterJoy.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace BetterJoy.Controller.Switch2;

/// <summary>
/// Discovers Nintendo Switch 2 controllers over BLE (both unpaired — pairing mode,
/// Switch2Connect style — and already paired, joycon2pc style) and hands
/// connected devices to the JoyconManager registration pipeline.
/// USB Switch 2 controllers are routed here by JoyconManager's HID hotplug.
/// </summary>
internal sealed class Switch2Manager : IDisposable
{
    private static Switch2Manager? _instance;

    private readonly ILogger? _logger;
    private readonly JoyconManager _manager;
    private readonly MainForm _form;
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly ConcurrentDictionary<ulong, byte> _connecting = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _pairedPollTask;
    private bool _disposed;

    public static Switch2Manager? Instance => _instance;

    private Switch2Manager(ILogger? logger, JoyconManager manager, MainForm form)
    {
        _logger = logger;
        _manager = manager;
        _form = form;

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(
            new BluetoothLEManufacturerData(Ns2Protocol.BleManufacturerId, new Windows.Storage.Streams.Buffer(0)));
        _watcher.Received += OnAdvertisementReceived;
    }

    public static void Start(ILogger? logger, JoyconManager manager, MainForm form)
    {
        try
        {
            _instance = new Switch2Manager(logger, manager, form);
            _instance.StartInternal();
        }
        catch (Exception e)
        {
            logger?.Log("Switch 2 support could not start (BLE unavailable?).", e);
            _instance = null;
        }
    }

    public static void Stop()
    {
        _instance?.Dispose();
        _instance = null;
    }

    private void StartInternal()
    {
        _watcher.Start();

        // Poll already-paired devices every few seconds.
        _pairedPollTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await ScanPairedDevicesAsync(_cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    _logger?.Log($"Switch 2 paired scan error: {e.Message}", LogLevel.Debug);
                }

                await Task.Delay(5000, _cts.Token);
            }
        }, _cts.Token);

        _logger?.Log("Switch 2 controller support enabled (BLE watcher + USB hotplug).");
    }

    // ------------------------------------------------------------------
    // BLE discovery
    // ------------------------------------------------------------------

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            // Manufacturer data: md[3:5]=VID, md[5:7]=PID, md[10:16]=paired MAC
            var md = args.Advertisement.ManufacturerData
                .FirstOrDefault(d => d.CompanyId == Ns2Protocol.BleManufacturerId);
            if (md == null)
            {
                return;
            }

            var data = new byte[md.Data.Length];
            var reader = Windows.Storage.Streams.DataReader.FromBuffer(md.Data);
            reader.ReadBytes(data);

            if (data.Length < 7)
            {
                return;
            }

            var productId = (ushort)(data[5] | (data[6] << 8));
            if (!Ns2Protocol.TryGetControllerType(productId, out _))
            {
                return;
            }

            var reconnectMac = data.Length >= 16 ? data[10..16] : null;
            var isPairingAd = reconnectMac == null || reconnectMac.AsSpan().IndexOfAnyExcept((byte)0) < 0;

            // The advertisement's address type must be carried into the connect:
            // NS2 controllers advertise with random addresses, and assuming
            // Public produces a device object that never answers GATT commands.
            _ = ConnectBleAsync(args.BluetoothAddress, productId, args.BluetoothAddressType, null, isPairingAd, reconnectMac, _cts.Token);
        }
        catch (Exception e)
        {
            _logger?.Log($"Switch 2 advertisement error: {e.Message}", LogLevel.Debug);
        }
    }

    private async Task ScanPairedDevicesAsync(CancellationToken ct)
    {
        var selector = GattDeviceService.GetDeviceSelectorFromUuid(Ns2Protocol.ServiceGuid);
        var devices = await DeviceInformation.FindAllAsync(selector);

        foreach (var info in devices)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryParseBleAddress(info.Id, out var address))
            {
                continue;
            }

            // Product ID from PnP characteristic (0x2A50) is resolved after
            // connect; Joy-Con 2 vs Pro 2 is decided inside the transport.
            // FromIdAsync carries the AEP identity (incl. address type), which
            // the raw address alone does not.
            _ = ConnectBleAsync(address, 0, null, info.Id, isPairingAd: false, reconnectMac: null, ct);
        }
    }

    private static bool TryParseBleAddress(string deviceId, out ulong address)
    {
        address = 0;

        // Device id ends with the MAC, e.g. "BluetoothLE#BluetoothLE...-aabbccddeeff"
        var idx = deviceId.LastIndexOf('-');
        if (idx < 0)
        {
            return false;
        }

        return ulong.TryParse(deviceId[(idx + 1)..], System.Globalization.NumberStyles.HexNumber, null, out address);
    }

    private async Task ConnectBleAsync(ulong address, ushort advertisedPid,
        BluetoothAddressType? addressType, string? deviceId, bool isPairingAd, byte[]? reconnectMac, CancellationToken ct)
    {
        if (!_connecting.TryAdd(address, 0))
        {
            return;
        }

        try
        {
            if (_manager.Controllers.Any(c => c.SerialOrMac.EndsWith($"{address:X12}", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            byte[]? cachedHostMac = null;
            if (advertisedPid != 0 && !isPairingAd && reconnectMac != null)
            {
                cachedHostMac = await GetHostMacAsync();
                if (cachedHostMac.Length == 6 && !reconnectMac.AsSpan().SequenceEqual(cachedHostMac))
                {
                    // Paired to a different host (e.g. the user's console) — ignore.
                    return;
                }
            }

            BluetoothLEDevice? device = deviceId != null
                ? await BluetoothLEDevice.FromIdAsync(deviceId)
                : addressType.HasValue
                    ? await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType.Value)
                    : await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device == null)
            {
                // Unpaired: request pairing (ConfirmOnly) — Switch2Connect flow.
                var info = await DeviceInformation.CreateFromIdAsync(
                    $"BluetoothLE#BluetoothLE{address:x12}",
                    ["System.Devices.Aep.ProtocolId", "System.Devices.Aep.Bluetooth.Le.Address"],
                    DeviceInformationKind.AssociationEndpoint);
                if (info?.Pairing != null && !info.Pairing.IsPaired)
                {
                    await info.Pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly);
                }

                device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                if (device == null)
                {
                    return;
                }
            }

            var productId = advertisedPid != 0 ? advertisedPid : await ReadPnpProductIdAsync(device, ct);

            var mac = new byte[6];
            for (var i = 0; i < 6; i++)
            {
                mac[i] = (byte)(address >> (8 * (5 - i)));
            }

            // Sync-button advertisement (or OS-unpaired enumerated link): the
            // controller drops the link when its pairing window closes unless
            // the host bonds to it in-protocol (0x15 MAC/LTK). Reconnect ads
            // whose reconnect MAC matches this host skip pairing.
            var needsHostPair = advertisedPid != 0
                ? isPairingAd
                : !(device.DeviceInformation?.Pairing?.IsPaired ?? false);
            var hostMac = needsHostPair ? cachedHostMac ?? await GetHostMacAsync() : [];
            if (needsHostPair && hostMac.Length < 6)
            {
                _logger?.Log("Switch 2 host pairing skipped: adapter MAC unavailable.", LogLevel.Warning);
            }

            var transport = new Switch2BleTransport(
                device, productId, mac, _logger, needsHostPair, hostMac);

            var type = Ns2Protocol.TryGetControllerType(productId, out var t)
                ? t
                : Joycon.ControllerType.Pro2; // PnP unreadable: default to a full pad

            var controller = new Switch2Device(
                _logger,
                _form,
                transport,
                $"ble://{address:x12}",
                $"{address:x12}",
                isUsb: false,
                padId: _manager.GetControllerPadId(type),
                type: type);

            _manager.RegisterController(controller);
        }
        catch (Exception e)
        {
            _logger?.Log($"Switch 2 BLE connect failed ({address:X12}): {e.Message}", LogLevel.Debug);
        }
        finally
        {
            _connecting.TryRemove(address, out _);
        }
    }

    private async Task<byte[]> GetHostMacAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null || adapter.BluetoothAddress == 0)
            {
                return [];
            }

            var mac = new byte[6];
            var addr = adapter.BluetoothAddress;
            for (var i = 0; i < 6; i++)
            {
                mac[i] = (byte)(addr >> (8 * i)); // little-endian, as the protocol wants
            }
            return mac;
        }
        catch (Exception e)
        {
            _logger?.Log($"Host BT adapter MAC unavailable: {e.Message}", LogLevel.Debug);
            return [];
        }
    }

    private async Task<ushort> ReadPnpProductIdAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        try
        {
            var result = await device.GetGattServicesForUuidAsync(
                new Guid("0000180a-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
            {
                return 0;
            }

            var service = result.Services[0];
            var chars = await service.GetCharacteristicsForUuidAsync(Ns2Protocol.PnpIdCharGuid, BluetoothCacheMode.Uncached);
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
            {
                return 0;
            }

            var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success || read.Value.Length < 7)
            {
                return 0;
            }

            // PnP ID (0x2A50), 7 bytes: [0]=vendorIdSource, [1:3]=VID,
            // [3:5]=product ID, [5:7]=version — little-endian fields.
            var buf = new byte[read.Value.Length];
            Windows.Storage.Streams.DataReader.FromBuffer(read.Value).ReadBytes(buf);

            return (ushort)(buf[3] | (buf[4] << 8));
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _cts.Cancel();
        try { _watcher.Stop(); } catch { }
        _cts.Dispose();
    }
}
