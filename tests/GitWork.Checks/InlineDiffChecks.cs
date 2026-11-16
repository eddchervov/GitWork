using GitWork.Core;

namespace GitWork.Checks;

internal static class InlineDiffChecks
{
    public static void Run(Action<bool, string> check)
    {
        var identical = InlineDiff.Compare("same", "same");
        check(identical.OldSpans.Count == 0 && identical.NewSpans.Count == 0, "Одинаковые строки не имеют inline diff");

        var insertion = InlineDiff.Compare("alpha omega", "alpha new omega");
        check(Slice("alpha new omega", insertion.NewSpans) == "new ", "Подсветка вставленного фрагмента");
        check(insertion.OldSpans.Count == 0, "Вставка не создаёт старый диапазон");

        var deletion = InlineDiff.Compare("alpha XYZ omega", "alpha omega");
        check(Slice("alpha XYZ omega", deletion.OldSpans) == "XYZ ", "Подсветка удалённого фрагмента");
        check(deletion.NewSpans.Count == 0, "Удаление не создаёт новый диапазон");

        var multipleEdits = InlineDiff.Compare("timeout 30 retries 1", "timeout 60 retries 3");
        check(Slice("timeout 30 retries 1", multipleEdits.OldSpans) == "30|1", "Раздельная подсветка нескольких изменений");
        check(Slice("timeout 60 retries 3", multipleEdits.NewSpans) == "60|3", "Не подсвечивается неизменная середина строки");

        var rename = InlineDiff.Compare("requestTime", "requestId");
        check(Slice("requestTime", rename.OldSpans) == "Time", "При переименовании сохраняется общий префикс");
        check(Slice("requestId", rename.NewSpans) == "Id", "При переименовании подсвечивается новая часть идентификатора");

        var whitespace = InlineDiff.Compare("a b", "a  b");
        check(Slice("a  b", whitespace.NewSpans) == " ", "Изменения пробелов сохраняются");

        var unicode = InlineDiff.Compare("a😀e\u0301z", "a😀E\u0301z");
        var unicodeSpan = unicode.NewSpans.Single();
        check("a😀E\u0301z".Substring(unicodeSpan.Start, unicodeSpan.Length) == "E\u0301", "Unicode-символы не разделяются посередине");
        check(!char.IsLowSurrogate("a😀E\u0301z"[unicodeSpan.Start]), "Диапазон начинается на границе суррогатной пары");

        var oversized = InlineDiff.Compare(new string('a', 4097), new string('b', 4097));
        check(oversized.OldSpans.Count == 0 && oversized.NewSpans.Count == 0, "Длинная строка обрабатывается без неограниченного diff");

        var bounded = InlineDiff.Compare("prefix old suffix", "prefix new suffix");
        check(IsValid("prefix old suffix", bounded.OldSpans) && IsValid("prefix new suffix", bounded.NewSpans), "Диапазоны inline diff отсортированы и в границах строки");
    }

    private static string Slice(string text, IReadOnlyList<InlineDiffSpan> spans) =>
        string.Join('|', spans.Select(span => text.Substring(span.Start, span.Length)));

    private static bool IsValid(string text, IReadOnlyList<InlineDiffSpan> spans)
    {
        var previousEnd = 0;

        foreach (var span in spans)
        {
            if (span.Length <= 0 || span.Start < previousEnd || span.Start + span.Length > text.Length)
            {
                return false;
            }

            previousEnd = span.Start + span.Length;
        }

        return true;
    }
}
