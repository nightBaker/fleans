using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using AutoMapper;
using Fleans.Application.Interfaces.Persistence.QueryServices;
using Fleans.Persistence;
using Microsoft.EntityFrameworkCore;
using ProjectTemplate.APPLICATION.Dtos.Queries;
using Sieve.Models;
using Sieve.Services;

namespace Fleans.Persistence.QueryServices;

public class QueryService<TEntity, T> : IQueryService<TEntity, T> where TEntity : class
{
    readonly IMapper _mapper;
    readonly ISieveProcessor _sieveProcessor;
    readonly DbSet<TEntity> _dbSet;

    public QueryService(FleansDbContext context, IMapper mapper, ISieveProcessor sieveProcessor)
    {
        _mapper = mapper;
        _sieveProcessor = sieveProcessor;
        _dbSet = context.Set<TEntity>();
    }

    public async Task<ListResultsDto<T>> GetAllAsync(SieveModel sieveModel)
    {
        var entities = GetAggreagteQueryable().AsNoTracking();
        entities = _sieveProcessor.Apply(sieveModel, entities);
        var sievedEntities = await entities.ToListAsync();

        return new ListResultsDto<T>
        {
            Items = sievedEntities.Select(entity => _mapper.Map<TEntity, T>(entity)).ToList(),
            TotalCount = await entities.CountAsync()
        };
    }

    public async Task<T> GetAsync(Expression<Func<TEntity, bool>> predicate)
    {
        var entity = await GetAggreagteQueryable().FirstOrDefaultAsync(predicate);
        return _mapper.Map<TEntity, T>(entity);
    }

    protected virtual IQueryable<TEntity> GetAggreagteQueryable() => _dbSet;
}
