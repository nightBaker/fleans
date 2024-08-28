using Fleans.Domain.SeedWork;
using ProjectTemplate.APPLICATION.Interfaces.Persistence;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace Fleans.Application.Interfaces.Persistence.CommandRepositories;

public interface ICommandRepository<T> where T : IAggregateRoot
{
    IUnitOfWork UnitOfWork { get; }
    Task<T?> GetAsync(Expression<Func<T, bool>> predicate);
    void Add(T item);
    T Remove(T item);
    Task<List<T>> GetListAsync(Expression<Func<T, bool>> predicate);
}
