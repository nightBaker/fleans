using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;

namespace Fleans.Domain;

public interface IActivityBuilder
{    
    ActivityBuilderResult Build();
}