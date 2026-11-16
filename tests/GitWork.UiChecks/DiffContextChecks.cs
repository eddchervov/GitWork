using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class DiffContextChecks
{
    private const string FilePath = "src/OpenCRM.Infrastructure.FileStorage/FileStorages/S3FileStorageProvider.cs";

    public static void Run()
    {
        InlineWorktreeUiChecks.RunAsync(async () =>
        {
            foreach (var isUnified in new[] { true, false })
            {
                await DiffContextChecks.CheckExpansionAsync(isUnified);
            }

            await DiffContextChecks.CheckStaleLoadAsync();
            await DiffContextChecks.CheckLoadFailureAsync();
            await DiffContextChecks.CheckEndOfFileAsync();
        });

        Console.WriteLine("OK: раскрытие контекста до начала файла, между блоками и до конца сохраняет строки и индексы diff в обеих раскладках.");
    }

    public static void RunPreview(Application application, string outputDirectory)
    {
        ThemeManager.Apply("Dark");
        Directory.CreateDirectory(outputDirectory);
        var view = new DiffView { FilePath = DiffContextChecks.FilePath, IsUnified = true };
        view.Header = new TextBlock { Text = DiffContextChecks.FilePath, VerticalAlignment = VerticalAlignment.Center };
        view.ShowDiff(DiffContextChecks.CreateDiff());
        var window = new Window
        {
            Title = "Git Work — раскрытие контекста diff",
            Content = view,
            Width = 1500,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.Show();
        window.UpdateLayout();
        DiffContextChecks.Capture(window, Path.Combine(outputDirectory, "context-before.png"));

        InlineWorktreeUiChecks.RunAsync(async () =>
        {
            foreach (var button in DiffContextChecks.GetAllContextButtons(view).ToArray())
            {
                DiffContextChecks.Click(button);
                await Task.Delay(30);
            }
        });

        window.UpdateLayout();
        DiffContextChecks.Capture(window, Path.Combine(outputDirectory, "context-expanded.png"));
        window.Close();
    }

    private static async Task CheckExpansionAsync(bool isUnified)
    {
        var view = new DiffView { FilePath = "Fixture.cs", IsUnified = isUnified };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        view.ShowDiff(DiffContextChecks.CreateDiff());
        window.Show();
        window.UpdateLayout();
        var patch = (string)typeof(DiffView).GetField("_patch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(view)!;
        view.NavigateChange(1);
        view.NavigateChange(1);
        if (view.UnifiedHunkCount != 2 || view.CurrentUnifiedHunkIndex != 1)
        {
            throw new InvalidOperationException("Проверочный diff должен содержать два исходных блока, выбран второй.");
        }

        if (DiffContextChecks.GetAllContextButtons(view).Count() != 3)
        {
            throw new InvalidOperationException($"Ожидались три промежутка строк, получено {DiffContextChecks.GetAllContextButtons(view).Count()}.");
        }

        var internalGapButton = DiffContextChecks.GetContextButtons(view).First(button => button.Content?.ToString()?.StartsWith("↓ ", StringComparison.Ordinal) == true);
        DiffContextChecks.Click(internalGapButton);
        await DiffContextChecks.WaitForAsync(() => DiffContextChecks.GetAllContextButtons(view).Count() == 3,
            "Первое раскрытие промежутка не завершилось.");
        var visibleContextCount = isUnified
            ? view.UnifiedLines.Count(line => line.Kind == UnifiedDiffLineKind.Context)
            : view.Lines.Count(line => line.Kind == DiffLineKind.Equal);
        if (visibleContextCount != 32)
        {
            throw new InvalidOperationException("Кнопка раскрытия на 20 строк должна добавить ровно двадцать контекстных строк.");
        }

        internalGapButton = DiffContextChecks.GetContextButtons(view).First(button => button.Content?.ToString()?.StartsWith("↓ ", StringComparison.Ordinal) == true);
        DiffContextChecks.Click(internalGapButton);
        await DiffContextChecks.WaitForAsync(() => DiffContextChecks.GetAllContextButtons(view).Count() == 2,
            "Повторное раскрытие промежутка не завершилось.");

        for (var gapIndex = 0; gapIndex < 2; gapIndex++)
        {
            DiffContextChecks.Click(DiffContextChecks.GetAllContextButtons(view).First());
            await DiffContextChecks.WaitForAsync(() => DiffContextChecks.GetAllContextButtons(view).Count() == 1 - gapIndex,
                "Раскрытие промежутка не завершилось.");
        }

        var visibleLines = isUnified
            ? view.UnifiedLines.Where(line => line.Kind == UnifiedDiffLineKind.Context)
                .Select(line => (line.OldNumber!.Value, line.NewNumber!.Value, line.Text))
                .ToArray()
            : view.Lines.Where(line => line.Kind == DiffLineKind.Equal)
                .Select(line => (line.OldNumber!.Value, line.NewNumber!.Value, line.NewText))
                .ToArray();
        if (visibleLines.Length != 98
            || visibleLines.Select(line => line.Item1).Distinct().Count() != 98
            || visibleLines.Select(line => line.Item2).Distinct().Count() != 98)
        {
            throw new InvalidOperationException("После раскрытия должны отображаться ровно 98 неизменённых строк без повторов.");
        }

        for (var lineNumber = 1; lineNumber <= 100; lineNumber++)
        {
            if (lineNumber is 30 or 70)
            {
                continue;
            }

            var sourceLine = DiffContextChecks.GetLine(lineNumber, false);
            if (!visibleLines.Any(line => line.Item1 == lineNumber && line.Item2 == lineNumber && line.Item3 == sourceLine))
            {
                throw new InvalidOperationException($"Строка {lineNumber} пропущена или отличается от исходного файла (Unified={isUnified}).");
            }
        }

        if (view.UnifiedHunkCount != 2 || view.CurrentUnifiedHunkIndex != 1 || patch != DiffContextChecks.ReadPatch(view))
        {
            throw new InvalidOperationException("Раскрытие контекста не должно менять исходный patch, число блоков или выбранный блок.");
        }

        window.Close();
    }

    private static async Task CheckStaleLoadAsync()
    {
        var pendingContext = new TaskCompletionSource<DiffContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = DiffContextChecks.CreateDiff() with { LoadContextAsync = () => pendingContext.Task };
        var replacement = DiffContextChecks.CreateDiff("replacement");
        var view = new DiffView { IsUnified = true };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        view.ShowDiff(original);
        window.Show();
        window.UpdateLayout();
        var button = DiffContextChecks.GetAllContextButtons(view).First();
        DiffContextChecks.Click(button);
        view.ShowDiff(replacement);
        pendingContext.SetResult(new DiffContext(
            Enumerable.Range(1, 100).Select(line => DiffContextChecks.GetLine(line, false)).ToArray(),
            Enumerable.Range(1, 100).Select(line => DiffContextChecks.GetLine(line, true)).ToArray()));
        await Task.Delay(50);
        if (!view.UnifiedLines.Any(line => line.Text == "replacement")
            || view.UnifiedLines.Any(line => line.Text.Contains("Protocol =", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Поздняя загрузка контекста не должна перезаписывать новый diff.");
        }

        window.Close();
    }

    private static async Task CheckLoadFailureAsync()
    {
        var diff = DiffContextChecks.CreateDiff() with
        {
            LoadContextAsync = () => Task.FromException<DiffContext>(new IOException("Не удалось загрузить строки файла."))
        };
        var view = new DiffView { IsUnified = true };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        view.ShowDiff(diff);
        window.Show();
        window.UpdateLayout();
        DiffContextChecks.Click(DiffContextChecks.GetAllContextButtons(view).First());
        await DiffContextChecks.WaitForAsync(() => ((Border)view.FindName("MessageBorder")).Visibility == Visibility.Visible,
            "Ошибка загрузки контекста должна отображаться пользователю.");
        if (!((TextBox)view.FindName("MessageText")).Text.Contains("Не удалось загрузить", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Сообщение об ошибке загрузки контекста потеряно.");
        }

        window.Close();
    }

    private static async Task CheckEndOfFileAsync()
    {
        var pending = new TaskCompletionSource<DiffContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diff = GitChangeService.ParseUnifiedDiff("@@ -1,2 +1,2 @@\n unchanged\n-old\n+new\n") with
        {
            LoadContextAsync = () => pending.Task
        };

        var view = new DiffView { IsUnified = true };
        view.ShowDiff(diff);
        if (DiffContextChecks.GetAllContextButtons(view).Any())
        {
            throw new InvalidOperationException("Стрелка до конца файла не должна показываться до проверки наличия скрытых строк.");
        }

        pending.SetResult(new DiffContext(["unchanged", "old"], ["unchanged", "new"]));
        await Task.Delay(50);
        if (DiffContextChecks.GetAllContextButtons(view).Any())
        {
            throw new InvalidOperationException("У последней строки файла не должно быть пустой стрелки раскрытия.");
        }
    }

    private static FileDiff CreateDiff(string changedText = "Protocol = new Uri(this._serviceUrl).Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS,")
    {
        var oldLines = Enumerable.Range(1, 100).Select(line => DiffContextChecks.GetLine(line, false)).ToArray();
        var newLines = oldLines.ToArray();
        newLines[29] = changedText;
        newLines[69] = "// replacement";
        var unifiedText = string.Join('\n',
            $"diff --git a/{DiffContextChecks.FilePath} b/{DiffContextChecks.FilePath}",
            "index 0000000..1111111 100644",
            $"--- a/{DiffContextChecks.FilePath}",
            $"+++ b/{DiffContextChecks.FilePath}",
            "@@ -27,7 +27,7 @@",
            " " + oldLines[26], " " + oldLines[27], " " + oldLines[28], "-" + oldLines[29], "+" + newLines[29],
            " " + oldLines[30], " " + oldLines[31], " " + oldLines[32],
            "@@ -67,7 +67,7 @@",
            " " + oldLines[66], " " + oldLines[67], " " + oldLines[68], "-" + oldLines[69], "+" + newLines[69],
            " " + oldLines[70], " " + oldLines[71], " " + oldLines[72]);
        var parsedDiff = GitChangeService.ParseUnifiedDiff(unifiedText);

        return parsedDiff with
        {
            LoadContextAsync = () => Task.FromResult(new DiffContext(oldLines, newLines))
        };
    }

    private static string GetLine(int lineNumber, bool isNew)
    {
        if (lineNumber == 30)
        {
            return isNew
                ? "Protocol = new Uri(this._serviceUrl).Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS,"
                : "Expires = DateTime.UtcNow.Add(expiration),";
        }

        if (lineNumber == 70)
        {
            return isNew ? "Verb = HttpVerb.GET" : "Verb = HttpVerb.POST";
        }

        return $"    // source line {lineNumber}";
    }

    private static string ReadPatch(DiffView view) => (string)typeof(DiffView)
        .GetField("_patch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(view)!;

    private static IEnumerable<Button> GetAllContextButtons(DiffView view)
    {
        var document = ((RichTextBox)view.FindName("CodeViewer")).Document;
        foreach (var block in document.Blocks)
        {
            if (block is BlockUIContainer container)
            {
                foreach (var button in DiffContextChecks.FindButtons(container.Child)
                    .Where(button => button.Content?.ToString()?.StartsWith("↕ ", StringComparison.Ordinal) == true))
                {
                    yield return button;
                }
            }

            if (block is Table table)
            {
                foreach (var row in table.RowGroups.SelectMany(group => group.Rows))
                {
                    foreach (var cell in row.Cells)
                    {
                        foreach (var cellBlock in cell.Blocks.OfType<BlockUIContainer>())
                        {
                            foreach (var button in DiffContextChecks.FindButtons(cellBlock.Child)
                                .Where(button => button.Content?.ToString()?.StartsWith("↕ ", StringComparison.Ordinal) == true))
                            {
                                yield return button;
                            }
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<Button> GetContextButtons(DiffView view)
    {
        var document = ((RichTextBox)view.FindName("CodeViewer")).Document;
        foreach (var block in document.Blocks)
        {
            var containers = block is BlockUIContainer directContainer
                ? [directContainer]
                : block is Table table
                    ? table.RowGroups.SelectMany(group => group.Rows).SelectMany(row => row.Cells)
                        .SelectMany(cell => cell.Blocks.OfType<BlockUIContainer>()).ToArray()
                    : [];
            foreach (var container in containers)
            {
                foreach (var button in DiffContextChecks.FindButtons(container.Child))
                {
                    yield return button;
                }
            }
        }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject? root)
    {
        if (root is Button button)
        {
            yield return button;
        }

        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                foreach (var childButton in DiffContextChecks.FindButtons(child as DependencyObject))
                {
                    yield return childButton;
                }
            }
        }

        if (root is Border border)
        {
            foreach (var childButton in DiffContextChecks.FindButtons(border.Child))
            {
                yield return childButton;
            }
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task WaitForAsync(Func<bool> condition, string errorMessage)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException(errorMessage);
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
