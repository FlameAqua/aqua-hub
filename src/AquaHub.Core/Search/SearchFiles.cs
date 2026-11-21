namespace AquaHub.Core.Search;

public sealed record SearchRoot(long Id, string Path, string Identity, bool Content)
{
    public override string ToString() => Path;
}

public sealed record SearchStamp(string Identity, long Length, long Modified, long Changed = 0);
public sealed record SearchHit(long RootId, string Path, SearchStamp Stamp, string Reason, string Snippet)
{
    public string Location { get; init; } = "";
    public override string ToString() => $"{Name}, {Reason}";
    public string Name => System.IO.Path.GetFileName(Path);
}

public interface ISearchFiles
{
    string Approve(string path);
    IDisposable DirectoryLease(string path, SearchRoot root);
    SearchFile Open(string path, SearchRoot root);
}

public abstract class SearchFile : IDisposable
{
    public abstract SearchStamp Stamp { get; }
    public abstract Stream Read();
    public abstract void Dispose();
}
