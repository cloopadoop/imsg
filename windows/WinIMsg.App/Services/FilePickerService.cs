using Windows.Storage.Pickers;

namespace WinIMsg.App.Services;

public interface IFilePickerService
{
    Task<string?> PickSingleFileAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default);
}

public sealed class WinUiFilePickerService(Func<IntPtr> windowHandleProvider) : IFilePickerService
{
    public async Task<string?> PickSingleFileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandleProvider());
        var file = await picker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return file?.Path;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandleProvider());
        var files = await picker.PickMultipleFilesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return files
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
    }
}
