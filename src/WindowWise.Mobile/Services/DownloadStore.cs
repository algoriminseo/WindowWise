namespace WindowWise.Mobile.Services;

/// <summary>
/// Downloads are written to the app's own folder first (where partial files can stay for resuming), then a
/// verified file is copied into the phone's public Downloads/WindowWise folder.
/// </summary>
public static class DownloadStore
{
    public static string WorkingFolder()
    {
#if ANDROID
        string folder = Android.App.Application.Context.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath
            ?? Path.Combine(FileSystem.AppDataDirectory, "downloads");
#else
        string folder = Path.Combine(FileSystem.AppDataDirectory, "downloads");
#endif
        Directory.CreateDirectory(folder);
        return folder;
    }

    // Keep the shared name; add " (1)", " (2)"… only when a finished file already uses it.
    public static string AvailablePath(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        for (int number = 1; File.Exists(path); number++) path = Path.Combine(folder, $"{stem} ({number}){extension}");
        return path;
    }

    // Copy a verified download into Downloads/WindowWise and remove the working copy.
    public static async Task PublishAsync(string path)
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            var resolver = Android.App.Application.Context.ContentResolver!;
            var values = new Android.Content.ContentValues();
            values.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, Path.GetFileName(path));
            values.Put(Android.Provider.MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryDownloads + "/WindowWise");
            values.Put(Android.Provider.MediaStore.IMediaColumns.IsPending, 1);
            var uri = resolver.Insert(Android.Provider.MediaStore.Downloads.ExternalContentUri, values)
                ?? throw new IOException("Could not create the file in Downloads.");
            try
            {
                await using (var target = resolver.OpenOutputStream(uri) ?? throw new IOException("Could not open the Downloads file."))
                await using (var source = File.OpenRead(path))
                    await source.CopyToAsync(target);
                values.Clear();
                values.Put(Android.Provider.MediaStore.IMediaColumns.IsPending, 0);
                resolver.Update(uri, values, null, null);
            }
            catch
            {
                resolver.Delete(uri, null, null);
                throw;
            }
            File.Delete(path);
        }
#else
        await Task.CompletedTask;
#endif
    }
}
