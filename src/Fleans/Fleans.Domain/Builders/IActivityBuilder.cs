using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;

namespace Fleans.Domain;

public interface IActivityBuilder
{
    IActivityBuilder WithId(Guid id);
    IActivity Build();
}