using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterJoy.Controller.Switch2;

/// <summary>
/// Transport channel to a Nintendo Switch 2 controller (BLE GATT or USB).
/// Runs the controller init sequence on connect and delivers raw input reports.
/// </summary>
internal interface ISwitch2Transport : IDisposable
{
    /// <summary>Raw input report (BLE notification body or USB report payload).</summary>
    event EventHandler<byte[]>? InputReportReceived;

    /// <summary>The link dropped (BLE disconnect or USB removal).</summary>
    event EventHandler? LinkDropped;

    /// <summary>Raised when the 3.5 mm headset jack plug state changes (Pro Controller 2).</summary>
    event EventHandler<bool>? HeadsetStateChanged;

    /// <summary>True when connected over USB; false for BLE.</summary>
    bool IsUsb { get; }

    /// <summary>True when BLE headset audio characteristics are available on this link.</summary>
    bool SupportsBleAudio { get; }

    /// <summary>True when a dedicated jack-state report is active on this link.</summary>
    bool HasHeadsetJackDetect { get; }

    /// <summary>True when a 3.5 mm plug is reported in the Pro Controller 2 jack.</summary>
    bool IsHeadsetConnected { get; }

    /// <summary>USB product ID or BLE PnP product ID of the controller.</summary>
    ushort ProductId { get; }

    /// <summary>Controller MAC (6 bytes) when known, otherwise empty.</summary>
    byte[] Mac { get; }

    /// <summary>Opens the link and runs the Switch 2 initialization sequence.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// BLE only: bonds the controller to this host (0x15 MAC + LTK exchange),
    /// needed when the link came from an unpaired advertisement so the
    /// controller does not power off when its pairing window closes.
    /// USB transports are no-ops.
    /// </summary>
    Task PairHostAsync(byte[] hostMac, CancellationToken ct);

    /// <summary>Sends a command frame and waits for its reply; returns the full reply frame.</summary>
    Task<byte[]> SendCommandAsync(byte command, byte subcommand, ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Reads controller memory (command 02:04); returns the payload.</summary>
    Task<byte[]> ReadMemoryAsync(int length, uint address, CancellationToken ct);

    /// <summary>Sets simple rumble state (implemented on BLE; USB support pending).</summary>
    void SetRumble(bool on);

    /// <summary>Sets the player LED pattern.</summary>
    Task SetPlayerLedAsync(int padId, CancellationToken ct);

    /// <summary>Writes an audio control/setup packet to the Pro 2 audio command characteristic.</summary>
    Task<bool> WriteBleAudioCommandAsync(byte[] payload, CancellationToken ct);

    /// <summary>Writes a 123-byte Opus chunk packet to the Pro 2 audio output characteristic.</summary>
    Task<bool> WriteBleAudioOutputAsync(byte[] payload, CancellationToken ct);

    /// <summary>Re-runs the Pro Controller 2 USB audio DSP initialization sequence (no-op on BLE or non-Pro2).</summary>
    Task ReinitializeUsbAudioAsync(CancellationToken ct);

    /// <summary>Stops the transport and releases the link.</summary>
    Task StopAsync();
}
