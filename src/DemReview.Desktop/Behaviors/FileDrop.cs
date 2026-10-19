using System.Windows.Input;
using Avalonia;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace DemReview.Desktop.Behaviors;

/// <summary>
/// Attached-поведение: принимает перетаскиваемый .dem файл и выполняет команду с его путём.
/// Использование: <c>behaviors:FileDrop.Command="{Binding LoadFileCommand}"</c>.
/// </summary>
public static class FileDrop
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<InputElement, ICommand?>("Command", typeof(FileDrop));

    static FileDrop()
    {
        CommandProperty.Changed.AddClassHandler<InputElement>((element, e) =>
        {
            element.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
            element.RemoveHandler(DragDrop.DropEvent, OnDrop);

            var enabled = e.NewValue is ICommand;
            DragDrop.SetAllowDrop(element, enabled);
            if (enabled)
            {
                element.AddHandler(DragDrop.DragOverEvent, OnDragOver);
                element.AddHandler(DragDrop.DropEvent, OnDrop);
            }
        });
    }

    public static ICommand? GetCommand(InputElement element) => element.GetValue(CommandProperty);

    public static void SetCommand(InputElement element, ICommand? value) => element.SetValue(CommandProperty, value);

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = FindDemoPath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not InputElement element || GetCommand(element) is not { } command || FindDemoPath(e) is not { } path)
            return;

        if (command.CanExecute(path))
            command.Execute(path);
        e.Handled = true;
    }

    private static string? FindDemoPath(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .FirstOrDefault(p => p is not null && p.EndsWith(".dem", StringComparison.OrdinalIgnoreCase));
}
