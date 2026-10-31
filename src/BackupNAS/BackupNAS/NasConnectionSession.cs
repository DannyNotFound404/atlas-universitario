namespace BackupNAS;

// Own only the connection this run created. Windows shares SMB sessions between
// applications; cancelling another application's connection can interrupt work.
internal sealed class NasConnectionSession
{
    private readonly Func<IReadOnlyList<string>> connectedPaths;
    private readonly Func<string, string, string, int> connect;
    private readonly Func<string, bool, int> disconnect;
    private string? ownedPath;

    internal NasConnectionSession() : this(WNetHelper.GetConnectedRemotePaths,
        (path, user, password) =>
        {
            var resource = new WNetHelper.NETRESOURCE { dwType = WNetHelper.RESOURCETYPE_DISK, lpRemoteName = path };
            return WNetHelper.WNetAddConnection2(ref resource, password, user, WNetHelper.CONNECT_TEMPORARY);
        }, (path, force) => WNetHelper.WNetCancelConnection2(path, 0, force)) { }

    internal NasConnectionSession(Func<IReadOnlyList<string>> connectedPaths,
        Func<string, string, string, int> connect, Func<string, bool, int> disconnect)
        => (this.connectedPaths, this.connect, this.disconnect) = (connectedPaths, connect, disconnect);

    internal bool OwnsConnection => ownedPath is not null;

    internal int Connect(string path, string user, string password)
    {
        if (ownedPath is not null) throw new InvalidOperationException("La sesión NAS ya está activa.");
        bool existed = connectedPaths().Any(value => value.Equals(path, StringComparison.OrdinalIgnoreCase));
        int result = connect(path, user, password);
        if (result == 0 && !existed) ownedPath = path;
        return result;
    }

    internal int Disconnect()
    {
        if (ownedPath is null) return 0;
        int result = disconnect(ownedPath, false);
        // An open handle may belong to another app that reused this connection.
        // Leave that session intact; never escalate to a forced disconnection.
        ownedPath = null;
        return result;
    }
}
