using System.Globalization;

namespace GitWork.Core;

public static class InlineDiff
{
    private const int MaximumTextLength = 4096;
    private const int MaximumMatrixCells = 200_000;

    public static InlineDiffResult Compare(string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);

        if (string.Equals(oldText, newText, StringComparison.Ordinal)
            || oldText.Length > InlineDiff.MaximumTextLength
            || newText.Length > InlineDiff.MaximumTextLength)
        {
            return InlineDiffResult.Empty;
        }

        var oldElements = InlineDiff.GetTextElements(oldText);
        var newElements = InlineDiff.GetTextElements(newText);
        var cellCount = (long)oldElements.Length * newElements.Length;

        if (cellCount > InlineDiff.MaximumMatrixCells)
        {
            return InlineDiff.CompareBySharedEdges(oldElements, newElements);
        }

        var columns = newElements.Length + 1;
        var lengths = new int[(oldElements.Length + 1) * columns];

        for (var oldIndex = oldElements.Length - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newElements.Length - 1; newIndex >= 0; newIndex--)
            {
                var cell = oldIndex * columns + newIndex;
                lengths[cell] = oldElements[oldIndex].Value == newElements[newIndex].Value
                    ? lengths[(oldIndex + 1) * columns + newIndex + 1] + 1
                    : Math.Max(lengths[(oldIndex + 1) * columns + newIndex], lengths[cell + 1]);
            }
        }

        var oldSpans = new List<InlineDiffSpan>();
        var newSpans = new List<InlineDiffSpan>();
        var oldCursor = 0;
        var newCursor = 0;
        var oldChangeStart = 0;
        var newChangeStart = 0;
        var hasChange = false;

        while (oldCursor < oldElements.Length || newCursor < newElements.Length)
        {
            if (oldCursor < oldElements.Length
                && newCursor < newElements.Length
                && oldElements[oldCursor].Value == newElements[newCursor].Value)
            {
                AddChangeSpans();
                oldCursor++;
                newCursor++;
                continue;
            }

            if (!hasChange)
            {
                oldChangeStart = oldCursor;
                newChangeStart = newCursor;
                hasChange = true;
            }

            var oldCanAdvance = oldCursor < oldElements.Length;
            var newCanAdvance = newCursor < newElements.Length;

            if (oldCanAdvance && (!newCanAdvance
                || lengths[(oldCursor + 1) * columns + newCursor] >= lengths[oldCursor * columns + newCursor + 1]))
            {
                oldCursor++;
            }
            else
            {
                newCursor++;
            }
        }

        AddChangeSpans();

        InlineDiff.ExpandNumericFragments(oldText, oldSpans);
        InlineDiff.ExpandNumericFragments(newText, newSpans);

        return new InlineDiffResult(oldSpans, newSpans);

        void AddChangeSpans()
        {
            if (!hasChange)
            {
                return;
            }

            InlineDiff.AddSpan(oldSpans, oldElements, oldChangeStart, oldCursor);
            InlineDiff.AddSpan(newSpans, newElements, newChangeStart, newCursor);
            hasChange = false;
        }
    }

    private static InlineDiffResult CompareBySharedEdges(
        TextElement[] oldElements,
        TextElement[] newElements)
    {
        var prefixLength = 0;

        while (prefixLength < oldElements.Length
            && prefixLength < newElements.Length
            && oldElements[prefixLength].Value == newElements[prefixLength].Value)
        {
            prefixLength++;
        }

        var suffixLength = 0;

        while (suffixLength < oldElements.Length - prefixLength
            && suffixLength < newElements.Length - prefixLength
            && oldElements[oldElements.Length - suffixLength - 1].Value
                == newElements[newElements.Length - suffixLength - 1].Value)
        {
            suffixLength++;
        }

        var oldSpans = new List<InlineDiffSpan>();
        var newSpans = new List<InlineDiffSpan>();
        InlineDiff.AddSpan(oldSpans, oldElements, prefixLength, oldElements.Length - suffixLength);
        InlineDiff.AddSpan(newSpans, newElements, prefixLength, newElements.Length - suffixLength);

        return new InlineDiffResult(oldSpans, newSpans);
    }

    private static TextElement[] GetTextElements(string text)
    {
        var elements = new List<TextElement>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            var start = enumerator.ElementIndex;
            var value = enumerator.GetTextElement();
            elements.Add(new TextElement(value, start, value.Length));
        }

        return elements.ToArray();
    }

    private static void AddSpan(
        List<InlineDiffSpan> spans,
        TextElement[] elements,
        int startElement,
        int endElement)
    {
        if (startElement == endElement)
        {
            return;
        }

        var start = elements[startElement].Start;
        var end = elements[endElement - 1].Start + elements[endElement - 1].Length;
        spans.Add(new InlineDiffSpan(start, end - start));
    }

    private static void ExpandNumericFragments(string text, List<InlineDiffSpan> spans)
    {
        var expandedSpans = new List<InlineDiffSpan>(spans.Count);

        foreach (var span in spans)
        {
            var start = span.Start;
            var end = start + span.Length;

            while (start > 0 && char.IsDigit(text[start - 1]))
            {
                start--;
            }

            while (end < text.Length && char.IsDigit(text[end]))
            {
                end++;
            }

            if (expandedSpans.Count > 0 && expandedSpans[^1].Start + expandedSpans[^1].Length >= start)
            {
                var previous = expandedSpans[^1];
                var mergedEnd = Math.Max(previous.Start + previous.Length, end);
                expandedSpans[^1] = new InlineDiffSpan(previous.Start, mergedEnd - previous.Start);
                continue;
            }

            expandedSpans.Add(new InlineDiffSpan(start, end - start));
        }

        spans.Clear();
        spans.AddRange(expandedSpans);
    }

    private readonly record struct TextElement(string Value, int Start, int Length);
}
