using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class DiffRenderingChecks
{
    public static void Run()
    {
        DiffRenderingChecks.CheckFragmentsAndSyntax();
        DiffRenderingChecks.CheckCommentState();
        DiffRenderingChecks.CheckFilePathInChanges();
        Console.WriteLine("OK: синтаксис и фрагменты правок видны в обеих раскладках, состояние комментариев разделено, путь файла передаётся после переноса заголовка.");
    }

    private static void CheckFragmentsAndSyntax()
    {
        var view = new DiffView { FilePath = "Options.cs", IsUnified = true };
        view.ShowDiff(GitChangeService.ParseUnifiedDiff("@@ -1,5 +1,5 @@\n public sealed class Options\n {\n-    public int Timeout = 30; public int Retries = 1;\n+    public int Timeout = 60; public int Retries = 3;\n     // Комментарий остаётся читаемым.\n }\n"));
        foreach (var unified in new[] { true, false })
        {
            view.IsUnified = unified;
            var document = ((RichTextBox)view.FindName("CodeViewer")).Document;
            var runs = DiffRenderingChecks.Runs(document.Blocks).ToArray();
            var added = string.Concat(runs.Where(run => run.Background == view.FindResource("DiffAddedFragmentBrush")).Select(run => run.Text));
            var removed = string.Concat(runs.Where(run => run.Background == view.FindResource("DiffRemovedFragmentBrush")).Select(run => run.Text));
            if (!added.Contains('6') || !added.Contains('3') || !removed.Contains('3') || !removed.Contains('1')
                || added.Contains("public", StringComparison.Ordinal) || added.Contains("Timeout", StringComparison.Ordinal)
                || !runs.Any(run => run.Text == "public" && run.Foreground == view.FindResource("SyntaxKeywordBrush"))
                || !runs.Any(run => run.Text == "Options" && run.Foreground == view.FindResource("SyntaxTypeBrush"))
                || !runs.Any(run => run.Text.Contains("Комментарий", StringComparison.Ordinal) && run.Foreground == view.FindResource("SyntaxCommentBrush")))
            {
                throw new InvalidOperationException("В обеих раскладках должны сочетаться синтаксис и фон только изменённых фрагментов.");
            }

            var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
            if (!text.Contains("public int Timeout = 30; public int Retries = 1;", StringComparison.Ordinal)
                || !text.Contains("public int Timeout = 60; public int Retries = 3;", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Разбиение на цветные фрагменты не должно менять копируемый текст кода.");
            }
        }
    }

    private static void CheckCommentState()
    {
        var view = new DiffView { FilePath = "Comments.cs", IsUnified = true };
        view.ShowDiff(GitChangeService.ParseUnifiedDiff("@@ -1,4 +1,4 @@\n-/* old comment\n+// new comment\n public class Sample { }\n-*/\n+// end\n var value = 1;\n@@ -100,1 +100,1 @@\n public class Later { }\n"));
        var runs = DiffRenderingChecks.Runs(((RichTextBox)view.FindName("CodeViewer")).Document.Blocks).ToArray();
        if (!runs.Any(run => run.Text == "Later" && run.Foreground == view.FindResource("SyntaxTypeBrush"))
            || !runs.Any(run => run.Text == "Sample" && run.Foreground == view.FindResource("SyntaxTypeBrush")))
        {
            throw new InvalidOperationException("Незакрытый комментарий в одном hunk не должен менять подсветку следующего разрозненного hunk.");
        }
    }

    private static void CheckFilePathInChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-work-diff-colors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Window? window = null;
        try
        {
            var git = new GitService();
            Task.Run(async () =>
            {
                await git.RunAsync(root, "init", "--initial-branch=main");
                await File.WriteAllTextAsync(Path.Combine(root, "Example.cs"), "public sealed class Example { public string Value = \"текст\"; }\n");
            }).GetAwaiter().GetResult();
            var view = new ChangesView("Fixture", [new ChangeSource("Fixture", root, [new ChangedFile("??", "Example.cs", null)])], git,
                "Example.cs", root, false);
            window = new Window { Content = view, Width = 1250, Height = 700 };
            window.Show();
            InlineWorktreeUiChecks.RunAsync(async () =>
            {
                var diff = (DiffView)view.FindName("DiffViewer");
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (((FrameworkElement)view.FindName("LoadingText")).Visibility != Visibility.Collapsed && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(25);
                }

                var runs = DiffRenderingChecks.Runs(((RichTextBox)diff.FindName("CodeViewer")).Document.Blocks).ToArray();
                if (diff.FilePath != "Example.cs" || !runs.Any(run => run.Text == "Example" && run.Foreground == diff.FindResource("SyntaxTypeBrush")))
                {
                    throw new InvalidOperationException("После переноса заголовка настоящий ChangesView должен передавать расширение файла и показывать типы цветом.");
                }
            });
        }
        finally
        {
            window?.Close();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, true);
        }
    }

    private static IEnumerable<Run> Runs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph)
            {
                foreach (var run in paragraph.Inlines.OfType<Run>())
                {
                    yield return run;
                }
            }

            if (block is Table table)
            {
                foreach (var cell in table.RowGroups.SelectMany(group => group.Rows).SelectMany(row => row.Cells))
                {
                    foreach (var run in DiffRenderingChecks.Runs(cell.Blocks))
                    {
                        yield return run;
                    }
                }
            }
        }
    }
}
