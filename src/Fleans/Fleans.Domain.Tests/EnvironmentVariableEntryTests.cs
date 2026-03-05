using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class EnvironmentVariableEntryTests
{
    // --- Validate() tests ---

    [TestMethod]
    public void Validate_ValidString_ReturnsNull()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "MY_VAR", Value = "hello", ValueType = "string" };

        // Act
        var result = entry.Validate();

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Validate_ValidInt_ReturnsNull()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "PORT", Value = "8080", ValueType = "int" };

        // Act
        var result = entry.Validate();

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Validate_ValidFloat_ReturnsNull()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "RATE", Value = "3.14", ValueType = "float" };

        // Act
        var result = entry.Validate();

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Validate_ValidBool_ReturnsNull()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "ENABLED", Value = "true", ValueType = "bool" };

        // Act
        var result = entry.Validate();

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Validate_EmptyName_ReturnsError()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "", Value = "val", ValueType = "string" };

        // Act
        var result = entry.Validate();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("Name is required"));
    }

    [TestMethod]
    public void Validate_WhitespaceName_ReturnsError()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "   ", Value = "val", ValueType = "string" };

        // Act
        var result = entry.Validate();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("Name is required"));
    }

    [TestMethod]
    public void Validate_InvalidType_ReturnsError()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "X", Value = "val", ValueType = "datetime" };

        // Act
        var result = entry.Validate();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("Invalid type"));
        Assert.IsTrue(result.Contains("datetime"));
    }

    [TestMethod]
    public void Validate_IntWithNonNumericValue_ReturnsError()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "PORT", Value = "abc", ValueType = "int" };

        // Act
        var result = entry.Validate();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("not a valid integer"));
    }

    [TestMethod]
    public void Validate_FloatWithNonNumericValue_ReturnsError()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "RATE", Value = "xyz", ValueType = "float" };

        // Act
        var result = entry.Validate();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("not a valid number"));
    }

    [TestMethod]
    public void Validate_BoolWithNonBoolValue_ReturnsError()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "FLAG", Value = "yes", ValueType = "bool" };

        // Act
        var result = entry.Validate();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("not a valid boolean"));
    }

    // --- GetTypedValue() tests ---

    [TestMethod]
    public void GetTypedValue_Int_ReturnsInt()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "PORT", Value = "8080", ValueType = "int" };

        // Act
        var result = entry.GetTypedValue();

        // Assert
        Assert.IsInstanceOfType<int>(result);
        Assert.AreEqual(8080, result);
    }

    [TestMethod]
    public void GetTypedValue_Float_ReturnsDouble()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "RATE", Value = "3.14", ValueType = "float" };

        // Act
        var result = entry.GetTypedValue();

        // Assert
        Assert.IsInstanceOfType<double>(result);
        Assert.AreEqual(3.14, result);
    }

    [TestMethod]
    public void GetTypedValue_Bool_ReturnsBool()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "FLAG", Value = "true", ValueType = "bool" };

        // Act
        var result = entry.GetTypedValue();

        // Assert
        Assert.IsInstanceOfType<bool>(result);
        Assert.AreEqual(true, result);
    }

    [TestMethod]
    public void GetTypedValue_String_ReturnsString()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "NAME", Value = "hello", ValueType = "string" };

        // Act
        var result = entry.GetTypedValue();

        // Assert
        Assert.IsInstanceOfType<string>(result);
        Assert.AreEqual("hello", result);
    }

    [TestMethod]
    public void GetTypedValue_NegativeInt_ReturnsNegativeInt()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "OFFSET", Value = "-42", ValueType = "int" };

        // Act
        var result = entry.GetTypedValue();

        // Assert
        Assert.IsInstanceOfType<int>(result);
        Assert.AreEqual(-42, result);
    }

    [TestMethod]
    public void GetTypedValue_BoolFalse_ReturnsFalse()
    {
        // Arrange
        var entry = new EnvironmentVariableEntry { Name = "FLAG", Value = "false", ValueType = "bool" };

        // Act
        var result = entry.GetTypedValue();

        // Assert
        Assert.IsInstanceOfType<bool>(result);
        Assert.AreEqual(false, result);
    }
}
