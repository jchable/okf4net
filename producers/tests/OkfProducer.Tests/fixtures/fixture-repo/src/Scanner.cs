namespace N;

/// <summary>Walks a repository and reports what it finds.</summary>
public class Scanner
{
    /// <summary>The root this scanner starts from.</summary>
    public string Root { get; }

    /// <summary>Scans the root. Nothing calls this, so adding an overload cannot move another concept.</summary>
    public void Scan()
    {
    }

    /// <summary>Normalizes one repository-relative path. The one call target this repository resolves.</summary>
    public string Normalize(string path)
    {
        return path;
    }

    private void Cache()
    {
    }

    /// <summary>Reads a legacy manifest. The symbol a mutation deletes; it is last in the file on purpose.</summary>
    public void Gone()
    {
    }
}
