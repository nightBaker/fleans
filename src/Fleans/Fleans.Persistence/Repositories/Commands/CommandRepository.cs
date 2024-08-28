using Fleans.Application.Interfaces.Persistence.CommandRepositories;
using Fleans.Domain.SeedWork;
using Microsoft.EntityFrameworkCore;
using ProjectTemplate.APPLICATION.Interfaces.Persistence;
using System.Linq.Expressions;

namespace Fleans.Persistence.Repositories.Commands;

public class CommandRepository<T> : ICommandRepository<T> where T : class, IAggregateRoot
{
    
    private readonly DbSet<T> _dbSet;

    public CommandRepository(FleansDbContext context, IUnitOfWork unitOfWork)
    {        
        UnitOfWork = unitOfWork;
        _dbSet = context.Set<T>();
    }

    public IUnitOfWork UnitOfWork { get; }

    public void Add(T item) => _dbSet.Add(item);

    public Task<T?> GetAsync(Expression<Func<T, bool>> predicate) =>
        GetAggreagteQueryable().FirstOrDefaultAsync(predicate);

    public Task<List<T>> GetListAsync(Expression<Func<T, bool>> predicate) =>
        GetAggreagteQueryable().Where(predicate).ToListAsync();

    public T Remove(T item) => _dbSet.Remove(item).Entity;

    protected virtual IQueryable<T> GetAggreagteQueryable() => _dbSet;
}
