using Bilgames.Core.Model;

namespace Bilgames.Core.Analysis;

/// <summary>English-language name heuristics: titles, nicknames and gendered first names.</summary>
public static class NameRules
{
    private static readonly Dictionary<string, Gender> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mr"] = Gender.Male, ["Mr."] = Gender.Male, ["Mister"] = Gender.Male, ["Sir"] = Gender.Male, ["Lord"] = Gender.Male,
        ["Uncle"] = Gender.Male, ["Father"] = Gender.Male, ["Brother"] = Gender.Male, ["King"] = Gender.Male, ["Prince"] = Gender.Male,
        ["Duke"] = Gender.Male, ["Count"] = Gender.Male, ["Baron"] = Gender.Male, ["Master"] = Gender.Male, ["Monsieur"] = Gender.Male,
        ["Mrs"] = Gender.Female, ["Mrs."] = Gender.Female, ["Miss"] = Gender.Female, ["Ms"] = Gender.Female, ["Ms."] = Gender.Female,
        ["Lady"] = Gender.Female, ["Aunt"] = Gender.Female, ["Mother"] = Gender.Female, ["Sister"] = Gender.Female, ["Queen"] = Gender.Female,
        ["Princess"] = Gender.Female, ["Duchess"] = Gender.Female, ["Countess"] = Gender.Female, ["Madam"] = Gender.Female, ["Madame"] = Gender.Female,
        ["Mademoiselle"] = Gender.Female, ["Dame"] = Gender.Female, ["Widow"] = Gender.Female, ["M"] = Gender.Male, ["Mme"] = Gender.Female,
        ["Dr"] = Gender.Unknown, ["Dr."] = Gender.Unknown, ["Doctor"] = Gender.Unknown, ["Captain"] = Gender.Unknown, ["Colonel"] = Gender.Unknown,
        ["Major"] = Gender.Unknown, ["Lieutenant"] = Gender.Unknown, ["General"] = Gender.Unknown, ["Professor"] = Gender.Unknown,
        ["Sergeant"] = Gender.Unknown, ["Judge"] = Gender.Unknown, ["Reverend"] = Gender.Unknown, ["Rev."] = Gender.Unknown,
        ["Officer"] = Gender.Unknown, ["Nurse"] = Gender.Unknown, ["Signor"] = Gender.Male, ["Signora"] = Gender.Female,
    };

    private static readonly HashSet<string> FemaleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Elizabeth", "Lizzy", "Eliza", "Jane", "Mary", "Catherine", "Kitty", "Lydia", "Charlotte", "Anne", "Ann", "Emma", "Julia", "Sarah",
        "Margaret", "Meg", "Jo", "Josephine", "Beth", "Amy", "Lucy", "Mina", "Helen", "Clare", "Clara", "Alice", "Rose", "Grace", "Harriet",
        "Emily", "Fanny", "Isabella", "Georgiana", "Caroline", "Louisa", "Maria", "Marianne", "Elinor", "Anna", "Ellen", "Agnes", "Susan",
        "Martha", "Judith", "Hannah", "Rebecca", "Ruth", "Esther", "Dorothy", "Edith", "Florence", "Mildred", "Clarisse", "Linda", "Polly",
        "Sally", "Becky", "Joanna", "Susannah", "Katherine", "Kate", "Nancy", "Peggy", "Molly", "Betsy", "Betty", "Daisy", "Lily", "Ivy",
        "Victoria", "Diana", "Laura", "Nora", "Olivia", "Sophia", "Sophie", "Violet", "Agatha", "Mildred", "Julia", "Clarisse",
        "Abigail", "Millie", "Milly", "Minnie", "Bertha", "Cora", "Eleanor", "Eva", "Evelyn", "Hester", "Irene", "Lucia", "Mabel", "Matilda", "Nell", "Pearl", "Phoebe",
    };

    private static readonly HashSet<string> MaleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tom", "Jim", "George", "William", "Will", "Bill", "John", "Jack", "James", "Thomas", "Charles", "Edward", "Robert", "Richard",
        "Henry", "Frederick", "Arthur", "Jonathan", "Abraham", "Victor", "Theodore", "Peter", "Paul", "Michael", "David", "Daniel",
        "Samuel", "Joseph", "Benjamin", "Andrew", "Hugh", "Walter", "Albert", "Alfred", "Harry", "Frank", "Fred", "Ned", "Sam", "Ben",
        "Joe", "Bob", "Dick", "Harold", "Ralph", "Roger", "Simon", "Stephen", "Philip", "Matthew", "Mark", "Luke", "Martin", "Lewis",
        "Louis", "Oliver", "Sidney", "Silas", "Edgar", "Herbert", "Ernest", "Ferdinand", "Winston", "Guy", "Hugo", "Leonard", "Eugene",
    };

    // Common nickname → full name pairs, used for merge suggestions.
    private static readonly Dictionary<string, string[]> Nicknames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Elizabeth"] = new[] { "Lizzy", "Lizzie", "Eliza", "Liz", "Beth", "Betsy", "Betty", "Bess", "Elsie" },
        ["Catherine"] = new[] { "Kitty", "Kate", "Cathy", "Cat" },
        ["Katherine"] = new[] { "Kitty", "Kate", "Kathy" },
        ["Margaret"] = new[] { "Meg", "Maggie", "Peggy", "Madge" },
        ["Josephine"] = new[] { "Jo", "Josie" },
        ["Mary"] = new[] { "Molly", "Polly", "Mamie" },
        ["Sarah"] = new[] { "Sally", "Sadie" },
        ["Rebecca"] = new[] { "Becky" },
        ["Theodore"] = new[] { "Teddy", "Laurie", "Ted" },
        ["William"] = new[] { "Will", "Bill", "Billy", "Willie" },
        ["Thomas"] = new[] { "Tom", "Tommy" },
        ["Huckleberry"] = new[] { "Huck" },
        ["James"] = new[] { "Jim", "Jimmy", "Jem" },
        ["Robert"] = new[] { "Bob", "Bobby", "Rob" },
        ["Richard"] = new[] { "Dick", "Rick" },
        ["Edward"] = new[] { "Ned", "Ed", "Teddy" },
        ["Henry"] = new[] { "Harry", "Hal", "Hank" },
        ["John"] = new[] { "Jack", "Johnny" },
        ["Frederick"] = new[] { "Fred", "Freddy" },
        ["Benjamin"] = new[] { "Ben" },
        ["Samuel"] = new[] { "Sam" },
        ["Joseph"] = new[] { "Joe" },
        ["Abraham"] = new[] { "Abe" },
        ["Susannah"] = new[] { "Sue", "Susie" },
        ["Eleanor"] = new[] { "Nell", "Nelly", "Ellie" },
        ["Fitzwilliam"] = new[] { "Fitz" },
        ["Mildred"] = new[] { "Millie", "Milly" },
        ["Wilhelmina"] = new[] { "Mina", "Minnie" },
        ["Christopher"] = new[] { "Kit", "Chris" },
    };

    public static bool IsTitle(string word) => Titles.ContainsKey(word.TrimEnd('.')) || Titles.ContainsKey(word);

    public static Gender TitleGender(string word) =>
        Titles.TryGetValue(word, out var g) || Titles.TryGetValue(word.TrimEnd('.'), out g) ? g : Gender.Unknown;

    public static Gender GuessFromName(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Gender.Unknown;
        var tg = TitleGender(parts[0]);
        if (tg != Gender.Unknown) return tg;
        var first = parts.FirstOrDefault(p => !IsTitle(p)) ?? parts[0];
        if (FemaleNames.Contains(first)) return Gender.Female;
        if (MaleNames.Contains(first)) return Gender.Male;
        return Gender.Unknown;
    }

    /// <summary>Name without titles, e.g. "Mr. Darcy" → "Darcy".</summary>
    public static string Core(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !IsTitle(p)));

    public static bool IsNicknameOf(string nick, string full)
    {
        var n = Core(nick).Split(' ').FirstOrDefault() ?? "";
        var f = Core(full).Split(' ').FirstOrDefault() ?? "";
        if (n.Length == 0 || f.Length == 0 || n.Equals(f, StringComparison.OrdinalIgnoreCase)) return false;
        if (Nicknames.TryGetValue(f, out var list) && list.Contains(n, StringComparer.OrdinalIgnoreCase)) return true;
        // "Eliza" for "Elizabeth": a prefix of at least four letters.
        return n.Length >= 4 && f.StartsWith(n, StringComparison.OrdinalIgnoreCase);
    }
}
