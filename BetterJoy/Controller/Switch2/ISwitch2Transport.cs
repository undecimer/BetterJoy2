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

    /// <summary>USB product ID or BLE PnP product ID of the controller.</summary>
    ushort ProductId { get; }

    /// <summary>Controller MAC (6 bytes) when known, otherwise empty.</summary>
    byte[] Mac { get; }

    /// <summary>Opens the link and runs the Switch 2 initialization sequence.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Sends a command frame and waits for its reply; returns the full reply frame.</summary>
    Task<byte[]> SendCommandAsync(byte command, byte subcommand, ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Reads controller memory (command 02:04); returns the payload.</summary>
    Task<byte[]> ReadMemoryAsync(int length, uint address, CancellationToken ct);

    /// <summary>Sets simple rumble state (implemented on BLE; USB support pending).</summary>
    void SetRumble(bool on);

    /// <summary>Sets the player LED pattern.</summary>
    Task SetPlayerLedAsync(int padId, CancellationToken ct);

    /// <summary>Stops the transport and releases the link.</summary>
    Task StopAsync();
}
