using System.IO;
using WindowWise.Models;

namespace WindowWise.Services;

/// <summary>
/// Files this peer offers to the connected peer. Only IDs, names, and sizes leave this PC; paths stay local.
/// Like local sharing, the peer sees the files only while sharing is started, and the list cannot change then.
/// The list outlives individual connections, so reconnecting keeps the same files and sharing state.
/// </summary>
public sealed class RemoteShareList
{
    private readonly object _lock = new();
    private readonly List<(SharedFileInfo Info, string Path)> _files = new();
    private bool _isSharing;

    public event EventHandler? Changed;

    // Every added file with its local path, for this peer's own view.
    public IReadOnlyList<(SharedFileInfo Info, string Path)> Entries
    {
        get { lock (_lock) return _files.ToArray(); }
    }

    // What the peer may see: nothing until sharing starts.
    public IReadOnlyList<SharedFileInfo> Files
    {
        get { lock (_lock) return _isSharing ? _files.Select(file => file.Info).ToArray() : []; }
    }

    public bool IsSharing
    {
        get { lock (_lock) return _isSharing; }
        set
        {
            lock (_lock)
            {
                if (_isSharing == value) return;
                _isSharing = value;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Adding the same path twice keeps the existing entry so the peer does not see duplicates.
    public SharedFileInfo Add(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists) throw new FileNotFoundException("The file to share does not exist.", file.FullName);
        SharedFileInfo info;
        lock (_lock)
        {
            if (_isSharing) throw new InvalidOperationException("Stop sharing before changing the list.");
            int index = _files.FindIndex(item => string.Equals(item.Path, file.FullName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) return _files[index].Info;
            info = new SharedFileInfo(Guid.NewGuid(), file.Name, file.Length);
            _files.Add((info, file.FullName));
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            if (_isSharing) throw new InvalidOperationException("Stop sharing before changing the list.");
            if (_files.RemoveAll(item => item.Info.Id == id) == 0) return false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // Resolve a requested ID to its local path while sharing. A file removed from disk counts as no longer shared.
    internal bool TryGetPath(Guid id, out string path)
    {
        lock (_lock)
        {
            int index = _isSharing ? _files.FindIndex(item => item.Info.Id == id) : -1;
            path = index >= 0 ? _files[index].Path : "";
        }
        return path.Length > 0 && File.Exists(path);
    }
}
