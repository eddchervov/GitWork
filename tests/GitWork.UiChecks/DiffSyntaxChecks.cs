using GitWork.App;

namespace GitWork.UiChecks;

internal static class DiffSyntaxChecks
{
    public static void Run()
    {
        var lines = new[]
        {
            "#define CHECK",
            "class Sample { int Value => this.Read(12);",
            "    string Text = @\"first \"\"quote\"\"",
            "second\"; /* first comment",
            "second comment */ string Raw = \"\"\"",
            "raw",
            "text",
            "\"\"\"; string Escaped = \"a \\\"quote\\\"\"; void Check() { var result = client.Execute(42); } }"
        };
        var spans = DiffSyntaxHighlighter.Highlight("Sample.cs", lines);
        var resourceKeys = spans.SelectMany(line => line).Select(span => span.ResourceKey).ToHashSet(StringComparer.Ordinal);
        var varPosition = lines[^1].IndexOf("var result", StringComparison.Ordinal);
        if (!spans[^1].Any(span => span.Start == varPosition && span.Length == 3 && span.ResourceKey == "SyntaxKeywordBrush"))
        {
            throw new InvalidOperationException("Контекстное ключевое слово var должно отличаться от имени типа.");
        }

        if (!resourceKeys.Contains("SyntaxDirectiveBrush")
            || !resourceKeys.Contains("SyntaxTypeBrush")
            || !resourceKeys.Contains("SyntaxMethodBrush")
            || !resourceKeys.Contains("SyntaxPropertyBrush")
            || !resourceKeys.Contains("SyntaxStringBrush")
            || !resourceKeys.Contains("SyntaxCommentBrush")
            || !resourceKeys.Contains("SyntaxNumberBrush")
            || !resourceKeys.Contains("SyntaxKeywordBrush"))
        {
            throw new InvalidOperationException("Подсветка C# должна распознавать директивы, типы, методы, свойства, строки, комментарии, числа и ключевые слова.");
        }

        var partialInitializerLines = new[]
        {
            "BucketName = _bucketName,",
            "Key = path,",
            "Expires = DateTime.UtcNow.Add(expiration),",
            "Protocol = new Uri(this._serviceUrl).Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS,",
            "Verb = HttpVerb.GET"
        };
        var partialInitializerSpans = DiffSyntaxHighlighter.Highlight("Partial.cs", partialInitializerLines);
        var expectedProperties = new[] { "BucketName", "Key", "Expires", "Protocol", "Verb" };
        for (var lineIndex = 0; lineIndex < expectedProperties.Length; lineIndex++)
        {
            var propertyName = expectedProperties[lineIndex];
            if (!partialInitializerSpans[lineIndex].Any(span => partialInitializerLines[lineIndex].Substring(span.Start, span.Length) == propertyName
                && span.ResourceKey == "SyntaxPropertyBrush"))
            {
                throw new InvalidOperationException($"Свойство {propertyName} должно распознаваться в частичном фрагменте инициализатора.");
            }
        }

        if (!partialInitializerSpans[2].Any(span => partialInitializerLines[2].Substring(span.Start, span.Length) == "DateTime"
                && span.ResourceKey == "SyntaxTypeBrush")
            || !partialInitializerSpans[2].Any(span => partialInitializerLines[2].Substring(span.Start, span.Length) == "Add"
                && span.ResourceKey == "SyntaxMethodBrush")
            || !partialInitializerSpans[3].Any(span => partialInitializerLines[3].Substring(span.Start, span.Length) == "Uri"
                && span.ResourceKey == "SyntaxTypeBrush"))
        {
            throw new InvalidOperationException("Типы и вызовы методов должны сохранять свои цвета в частичном инициализаторе.");
        }

        var fullInitializerLines = new[]
        {
            "var request = new Request",
            "{",
            "    BucketName = bucketName,",
            "    Verb = HttpVerb.GET",
            "};"
        };
        var fullInitializerSpans = DiffSyntaxHighlighter.Highlight("Full.cs", fullInitializerLines);
        if (!fullInitializerSpans[2].Any(span => fullInitializerLines[2].Substring(span.Start, span.Length) == "BucketName"
                && span.ResourceKey == "SyntaxPropertyBrush")
            || !fullInitializerSpans[3].Any(span => fullInitializerLines[3].Substring(span.Start, span.Length) == "HttpVerb"
                && span.ResourceKey == "SyntaxTypeBrush"))
        {
            throw new InvalidOperationException("Инициализатор с полным контекстом должен отличать свойства от типов.");
        }

        if (!spans[5].Any(span => span.Start == 0 && span.Length == lines[5].Length && span.ResourceKey == "SyntaxStringBrush")
            || spans[^1].Any(span => lines[^1].Substring(span.Start, span.Length) is "client" or "result"
                && span.ResourceKey is "SyntaxMethodBrush" or "SyntaxTypeBrush")
            || !spans[^1].Any(span => lines[^1].Substring(span.Start, span.Length) == "Execute"
                && span.ResourceKey == "SyntaxMethodBrush"))
        {
            throw new InvalidOperationException("Raw-строки должны подсвечиваться целиком, а у вызова метода должна выделяться только его имя.");
        }

        for (var lineIndex = 0; lineIndex < spans.Length; lineIndex++)
        {
            var lineSpans = spans[lineIndex];
            var previousEnd = 0;
            var reconstructed = new System.Text.StringBuilder();
            foreach (var span in lineSpans)
            {
                if (span.Start < previousEnd || span.Length <= 0 || span.Start + span.Length > lines[lineIndex].Length)
                {
                    throw new InvalidOperationException("Диапазоны подсветки должны быть отсортированы, не пересекаться и оставаться внутри строки.");
                }

                reconstructed.Append(lines[lineIndex], previousEnd, span.Start - previousEnd);
                reconstructed.Append(lines[lineIndex], span.Start, span.Length);
                previousEnd = span.Start + span.Length;
            }

            reconstructed.Append(lines[lineIndex], previousEnd, lines[lineIndex].Length - previousEnd);
            if (!string.Equals(reconstructed.ToString(), lines[lineIndex], StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Подсветка не должна менять исходный текст строки.");
            }
        }

        if (DiffSyntaxHighlighter.Highlight("data.json", ["{\"key\": true}"]).Any(line => line.Count > 0))
        {
            throw new InvalidOperationException("Подсветчик C# не должен раскрашивать файлы других форматов.");
        }

        Console.WriteLine("OK: подсветка синтаксиса C# сохраняет исходные строки и корректно делит многострочные токены.");
    }
}
