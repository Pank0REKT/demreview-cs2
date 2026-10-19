using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DemReview.Application.Localization;

namespace DemReview.Desktop.Services;

/// <summary>Доступ к UI-потоку из ViewModel без прямой зависимости от Avalonia.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}

/// <summary><see cref="IProgress{T}"/>, доставляющий обновления в UI-поток через Dispatcher.UIThread.</summary>
public sealed class UiProgress<T>(IUiDispatcher dispatcher, Action<T> handler) : IProgress<T>
{
    public void Report(T value) => dispatcher.Post(() => handler(value));
}

/// <summary>Ссылка на главное окно — для диалогов, которым нужен TopLevel.</summary>
public sealed class MainWindowAccessor
{
    public Window? Window { get; set; }
}

public interface IFileDialogService
{
    Task<string?> PickDemoFileAsync();

    /// <summary>Диалог сохранения профиля (Markdown или JSON); возвращает локальный путь или null.</summary>
    Task<string?> PickProfileSavePathAsync(string suggestedName);
}

public sealed class AvaloniaFileDialogService(MainWindowAccessor windowAccessor) : IFileDialogService
{
    private static FilePickerFileType DemoFileType => new(Loc.T("Демки CS2 (*.dem)", "CS2 demos (*.dem)")) { Patterns = ["*.dem"] };

    public async Task<string?> PickDemoFileAsync()
    {
        if (windowAccessor.Window?.StorageProvider is not { } storage)
            return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Открыть демку CS2", "Open a CS2 demo"),
            AllowMultiple = false,
            FileTypeFilter = [DemoFileType, FilePickerFileTypes.All],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickProfileSavePathAsync(string suggestedName)
    {
        if (windowAccessor.Window?.StorageProvider is not { } storage)
            return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Экспорт профиля игрока", "Export player profile"),
            SuggestedFileName = suggestedName,
            DefaultExtension = "md",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown (*.md)") { Patterns = ["*.md"] },
                new FilePickerFileType("JSON (*.json)") { Patterns = ["*.json"] },
            ],
        });

        return file?.TryGetLocalPath();
    }
}
