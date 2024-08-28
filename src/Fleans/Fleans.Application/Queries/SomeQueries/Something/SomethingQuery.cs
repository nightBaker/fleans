using MediatR;

namespace ProjectTemplate.APPLICATION.Dtos.Queries.SomeQueries.Something
{
    public class SomethingQuery : IRequest<SomeDto>
    {
        public long SomeId { get; set; }
    }
}
