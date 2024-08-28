using System.Linq.Expressions;
using ProjectTemplate.APPLICATION.Dtos.Queries;
using Sieve.Models;

namespace Fleans.Application.Interfaces.Persistence.QueryServices;

public interface IQueryService<TEntity, T>
{
    Task<T> GetAsync(Expression<Func<TEntity, bool>> predicate);
    Task<ListResultsDto<T>> GetAllAsync(SieveModel sieveModel);
}
