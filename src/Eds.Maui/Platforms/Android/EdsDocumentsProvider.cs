using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Eds.Core.App;
using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;
using IFileSystem = Eds.Core.Fs.Vfs.IFileSystem;
using Eds.Maui.Services;

namespace Eds.Maui.Platforms.Droid;

/// <summary>
/// Storage Access Framework provider that exposes the currently-open EDS locations
/// (their decrypted virtual filesystems) to other apps — so any SAF-aware file
/// browser can list, read, write, create and delete inside an open container/EncFS.
/// Files are decrypted to a temp copy on open and re-encrypted on close.
///
/// <para>Registered in AndroidManifest.xml as a <c>&lt;provider&gt;</c> with the
/// <c>DOCUMENTS_PROVIDER</c> intent filter. Document ids are
/// <c>"{locationId}|{vfsPath}"</c>. Only <b>open</b> locations are served.</para>
/// </summary>
[Register("com.sovworks.edslite.net.EdsDocumentsProvider")]
public class EdsDocumentsProvider : DocumentsProvider
{
    private static readonly string[] DefaultRootProjection =
    {
        DocumentsContract.Root.ColumnRootId!, DocumentsContract.Root.ColumnFlags!,
        DocumentsContract.Root.ColumnTitle!, DocumentsContract.Root.ColumnDocumentId!,
        DocumentsContract.Root.ColumnIcon!, DocumentsContract.Root.ColumnMimeTypes!,
    };

    private static readonly string[] DefaultDocProjection =
    {
        DocumentsContract.Document.ColumnDocumentId!, DocumentsContract.Document.ColumnDisplayName!,
        DocumentsContract.Document.ColumnMimeType!, DocumentsContract.Document.ColumnSize!,
        DocumentsContract.Document.ColumnLastModified!, DocumentsContract.Document.ColumnFlags!,
    };

    public override bool OnCreate() => true;

    public override ICursor QueryRoots(string[]? projection)
    {
        var cursor = new MatrixCursor(projection ?? DefaultRootProjection);
        var app = AppServices.Get<EdsAppController>();
        if (app == null) return cursor;

        foreach (var loc in app.GetRegisteredLocations())
        {
            if (!loc.IsFileSystemOpen()) continue;
            var row = cursor.NewRow();
            row.Add(DocumentsContract.Root.ColumnRootId, loc.GetId());
            row.Add(DocumentsContract.Root.ColumnDocumentId, loc.GetId() + "|/");
            row.Add(DocumentsContract.Root.ColumnTitle, "EDS: " + loc.GetTitle());
            row.Add(DocumentsContract.Root.ColumnFlags,
                (int)(DocumentContractRootFlags.SupportsCreate | DocumentContractRootFlags.LocalOnly));
            row.Add(DocumentsContract.Root.ColumnIcon, global::Android.Resource.Drawable.StatSysDownload);
            row.Add(DocumentsContract.Root.ColumnMimeTypes, "*/*");
        }
        return cursor;
    }

    public override ICursor QueryDocument(string? documentId, string[]? projection)
    {
        var cursor = new MatrixCursor(projection ?? DefaultDocProjection);
        var (app, locId, path) = Resolve(documentId);
        var fs = FsFor(app, locId);
        AddDocRow(cursor, locId, fs.GetPath(path));
        return cursor;
    }

    public override ICursor QueryChildDocuments(string? parentDocumentId, string[]? projection, string? sortOrder)
    {
        var cursor = new MatrixCursor(projection ?? DefaultDocProjection);
        var (app, locId, path) = Resolve(parentDocumentId);
        var fs = FsFor(app, locId);
        var dir = fs.GetPath(path).GetDirectory();
        using var contents = dir.List();
        foreach (var child in contents)
            AddDocRow(cursor, locId, child);
        return cursor;
    }

    public override ParcelFileDescriptor? OpenDocument(string? documentId, string? mode, CancellationSignal? signal)
    {
        var (app, locId, path) = Resolve(documentId);
        var fs = FsFor(app, locId);
        var file = fs.GetPath(path).GetFile();

        var handle = app.PrepareTempFile(file);
        bool writable = mode != null && mode.Contains('w');

        var pmode = !writable ? ParcelFileMode.ReadOnly
                  : (mode!.Contains('r') ? ParcelFileMode.ReadWrite : ParcelFileMode.WriteOnly);

        var javaFile = new Java.IO.File(handle.TempPath);
        var handler = new Handler(Looper.MainLooper!);

        return ParcelFileDescriptor.Open(javaFile, pmode, handler, new WriteBackOnClose(() =>
        {
            try { if (writable) app.SaveTempChanges(handle); }
            finally { app.ClearTempFile(handle); }
        }));
    }

    public override string? CreateDocument(string? parentDocumentId, string? mimeType, string? displayName)
    {
        var (app, locId, path) = Resolve(parentDocumentId);
        var fs = FsFor(app, locId);
        var dir = fs.GetPath(path).GetDirectory();
        var name = displayName ?? "untitled";

        if (mimeType == DocumentsContract.Document.MimeTypeDir)
        {
            var nd = dir.CreateDirectory(name);
            return locId + "|" + nd.Path.PathString;
        }
        var nf = dir.CreateFile(name);
        return locId + "|" + nf.Path.PathString;
    }

    public override void DeleteDocument(string? documentId)
    {
        var (app, locId, path) = Resolve(documentId);
        var fs = FsFor(app, locId);
        var p = fs.GetPath(path);
        app.DeleteAsync(new[] { p }).GetAwaiter().GetResult();
    }

    // ---- helpers -------------------------------------------------------

    private static (EdsAppController app, string locId, string path) Resolve(string? documentId)
    {
        var app = AppServices.Get<EdsAppController>()
                  ?? throw new Java.IO.FileNotFoundException("Application not ready");
        var id = documentId ?? "";
        int i = id.IndexOf('|');
        string locId = i < 0 ? id : id.Substring(0, i);
        string path = i < 0 ? "/" : id.Substring(i + 1);
        if (string.IsNullOrEmpty(path)) path = "/";
        return (app, locId, path);
    }

    private static IFileSystem FsFor(EdsAppController app, string locId)
    {
        var loc = app.FindLocation(locId);
        if (loc == null || !loc.IsFileSystemOpen())
            throw new Java.IO.FileNotFoundException("Location is not open");
        return app.GetFileSystem(loc);
    }

    private static void AddDocRow(MatrixCursor cursor, string locId, IPath path)
    {
        bool isDir = path.IsDirectory();
        string name = new StringPathUtil(path.PathString).GetFileName();
        if (string.IsNullOrEmpty(name)) name = "/";

        long size = 0;
        long modified = 0;
        try
        {
            if (!isDir)
            {
                var f = path.GetFile();
                size = f.GetSize();
                modified = f.GetLastModified().ToUnixTimeMilliseconds();
            }
        }
        catch { /* leave defaults */ }

        var flags = isDir
            ? (int)(DocumentContractFlags.DirSupportsCreate | DocumentContractFlags.SupportsDelete)
            : (int)(DocumentContractFlags.SupportsWrite | DocumentContractFlags.SupportsDelete);

        var row = cursor.NewRow();
        row.Add(DocumentsContract.Document.ColumnDocumentId, locId + "|" + path.PathString);
        row.Add(DocumentsContract.Document.ColumnDisplayName, name);
        row.Add(DocumentsContract.Document.ColumnMimeType,
            isDir ? DocumentsContract.Document.MimeTypeDir : GuessMime(name));
        row.Add(DocumentsContract.Document.ColumnSize, size);
        row.Add(DocumentsContract.Document.ColumnLastModified, modified);
        row.Add(DocumentsContract.Document.ColumnFlags, flags);
    }

    private static string GuessMime(string name)
    {
        int dot = name.LastIndexOf('.');
        string ext = dot < 0 ? "" : name[(dot + 1)..].ToLowerInvariant();
        return ext switch
        {
            "txt" or "log" or "md" or "csv" => "text/plain",
            "pdf" => "application/pdf",
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "html" or "htm" => "text/html",
            "json" => "application/json",
            "zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }

    private sealed class WriteBackOnClose : Java.Lang.Object, ParcelFileDescriptor.IOnCloseListener
    {
        private readonly Action _action;
        public WriteBackOnClose(Action action) => _action = action;
        public void OnClose(Java.IO.IOException? e) => _action();
    }
}
