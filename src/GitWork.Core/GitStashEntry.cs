namespace GitWork.Core;

public sealed record GitStashEntry(string Hash, string Reference, string Message, DateTimeOffset CreatedAt);
