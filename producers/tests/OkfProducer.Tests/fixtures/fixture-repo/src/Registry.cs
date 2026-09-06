namespace N;

/// <summary>Keeps the scanners one run knows about.</summary>
public class Registry
{
    /// <summary>Registers a scanner against the repository root.</summary>
    public string Register(Scanner scanner)
    {
        return scanner.Normalize("/");
    }

    /// <summary>Registers a scanner against an explicit root. The second half of the merged overload pair.</summary>
    public string Register(Scanner scanner, string root)
    {
        return scanner.Normalize(root);
    }

    public int Count(string raw)
    {
        return int.Parse(raw);
    }
}
