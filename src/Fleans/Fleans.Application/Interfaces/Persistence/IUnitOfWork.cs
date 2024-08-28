using System.Threading;
using System.Threading.Tasks;

namespace ProjectTemplate.APPLICATION.Interfaces.Persistence
{
    public interface IUnitOfWork
    {
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}
