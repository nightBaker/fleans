namespace Fleans.Domain.Tests;

[TestClass]
public class CustomTaskVersionsTests
{
    [TestMethod]
    [DataRow("1.4.0", "1.4.0")]
    [DataRow("0.0.1", "0.0.1")]
    [DataRow("2.0.0-rc.1", "2.0.0-rc.1")]
    [DataRow("1.4.0+abc123", "1.4.0")]
    [DataRow("1.4.0-beta+abc.123", "1.4.0-beta")]
    public void Valid_versions_lose_their_build_metadata(string version, string expected)
    {
        Assert.IsTrue(CustomTaskVersions.TryNormalize(version, out var normalized));
        Assert.AreEqual(expected, normalized);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("latest")]
    [DataRow("1")]
    [DataRow("1.4")]
    [DataRow("v1.4.0")]
    [DataRow("01.4.0")]
    [DataRow("1.4.0-")]
    [DataRow("1.4.0-01")]
    [DataRow("1.4.0+")]
    public void Anything_else_is_not_a_version(string version)
    {
        Assert.IsFalse(CustomTaskVersions.TryNormalize(version, out _));
    }

    [TestMethod]
    public void A_version_over_the_length_limit_is_refused()
    {
        Assert.IsFalse(CustomTaskVersions.TryNormalize("1.0.0-" + new string('a', CustomTaskVersions.MaxLength), out _));
    }

    [TestMethod]
    public void Newest_first_puts_stable_before_prerelease_before_no_version()
    {
        // Arrange
        string?[] shuffled = [null, "1.9.0", "2.0.0-rc.1", "1.10.0", "2.0.0-rc.2", "2.0.0-alpha", "2.0.0-rc.1.1", "1.9.0-rc.10", "1.9.0-rc.9"];

        // Act
        var ordered = shuffled.Order(CustomTaskVersions.NewestFirst).ToArray();

        // Assert
        CollectionAssert.AreEqual(
            new string?[] { "1.10.0", "1.9.0", "2.0.0-rc.2", "2.0.0-rc.1.1", "2.0.0-rc.1", "2.0.0-alpha", "1.9.0-rc.10", "1.9.0-rc.9", null },
            ordered);
    }

    [TestMethod]
    public void Latest_selects_everything_of_the_newest_version()
    {
        // Arrange
        (string Worker, string? Version)[] workers = [("a", "1.0.0"), ("b", "2.0.0"), ("c", "2.0.0"), ("d", "3.0.0-rc.1"), ("e", null)];

        // Act
        var selected = CustomTaskVersions.SelectGroup(workers, w => w.Version, requested: null);

        // Assert
        CollectionAssert.AreEqual(new[] { "b", "c" }, selected.Select(w => w.Worker).ToArray());
    }

    [TestMethod]
    public void Latest_falls_back_to_a_prerelease_and_then_to_no_version()
    {
        (string Worker, string? Version)[] prerelease = [("a", "3.0.0-rc.1"), ("b", null)];
        (string Worker, string? Version)[] unversioned = [("a", null), ("b", null)];

        Assert.AreEqual("a", CustomTaskVersions.SelectGroup(prerelease, w => w.Version, null).Single().Worker);
        Assert.HasCount(2, CustomTaskVersions.SelectGroup(unversioned, w => w.Version, null));
    }

    [TestMethod]
    public void An_exact_version_selects_only_itself_and_never_a_worker_without_one()
    {
        // Arrange
        (string Worker, string? Version)[] workers = [("a", "1.0.0"), ("b", "2.0.0"), ("c", null)];

        // Act + Assert
        Assert.AreEqual("a", CustomTaskVersions.SelectGroup(workers, w => w.Version, "1.0.0").Single().Worker);
        Assert.IsEmpty(CustomTaskVersions.SelectGroup(workers, w => w.Version, "9.9.9"));
    }
}
