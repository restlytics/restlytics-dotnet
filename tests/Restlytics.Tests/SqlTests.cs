using Restlytics.AspNetCore;
using Xunit;

namespace Restlytics.Tests;

public class SqlTests
{
    [Fact]
    public void StripsNumericLiterals()
    {
        Assert.Equal(
            "select * from users where id = ?",
            Sql.Normalize("SELECT * FROM users WHERE id = 1"));
    }

    [Fact]
    public void StripsStringLiterals()
    {
        Assert.Equal(
            "select * from users where email = ?",
            Sql.Normalize("SELECT * FROM users WHERE email = 'alice@example.com'"));
    }

    [Fact]
    public void TwoDifferentLiteralsProduceTheSameTemplate()
    {
        // The whole point: id=1 and id=2 must group together (N+1 fingerprint).
        string a = Sql.Normalize("SELECT * FROM users WHERE id = 1");
        string b = Sql.Normalize("SELECT * FROM users WHERE id = 2");
        Assert.Equal(a, b);
    }

    [Fact]
    public void CollapsesInListsToSinglePlaceholder()
    {
        Assert.Equal(
            "select * from users where id in (?)",
            Sql.Normalize("SELECT * FROM users WHERE id IN (1, 2, 3, 4, 5)"));

        // Varying length lists must collapse to the SAME template.
        string shortList = Sql.Normalize("SELECT * FROM users WHERE id IN (1, 2)");
        string longList = Sql.Normalize("SELECT * FROM users WHERE id IN (1, 2, 3, 4)");
        Assert.Equal(shortList, longList);
    }

    [Fact]
    public void CollapsesExistingPlaceholdersAndInLists()
    {
        Assert.Equal(
            "select * from t where id in (?)",
            Sql.Normalize("SELECT * FROM t WHERE id IN (?, ?, ?)"));
    }

    [Fact]
    public void SquashesWhitespaceAndNewlines()
    {
        Assert.Equal(
            "select id from users where active = ?",
            Sql.Normalize("SELECT   id\n  FROM users\n\tWHERE active   =   1"));
    }

    [Fact]
    public void CollapsesValuesTuples()
    {
        Assert.Equal(
            "insert into t (a, b) values (?)",
            Sql.Normalize("INSERT INTO t (a, b) VALUES (1, 2), (3, 4), (5, 6)"));
    }

    [Fact]
    public void HandlesNamedAndPositionalBindings()
    {
        Assert.Equal(
            "select * from users where id = ? and name = ?",
            Sql.Normalize("SELECT * FROM users WHERE id = :id AND name = $1"));
    }

    [Fact]
    public void DoesNotMangleIdentifiersWithTrailingDigits()
    {
        // column2 must stay column2 (it's an identifier, not a literal).
        string outSql = Sql.Normalize("SELECT column2 FROM table1 WHERE column2 = 5");
        Assert.Contains("column2", outSql);
        Assert.Contains("= ?", outSql);
    }

    [Fact]
    public void StripsDecimalAndHexLiterals()
    {
        Assert.Equal(
            "select * from t where price > ? and flag = ?",
            Sql.Normalize("SELECT * FROM t WHERE price > 19.99 AND flag = 0xFF"));
    }

    [Fact]
    public void EmptyAndNullAreEmpty()
    {
        Assert.Equal(string.Empty, Sql.Normalize(""));
        Assert.Equal(string.Empty, Sql.Normalize(null));
    }
}
