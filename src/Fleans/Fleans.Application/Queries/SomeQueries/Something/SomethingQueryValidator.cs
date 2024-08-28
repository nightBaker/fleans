using FluentValidation;
using MediatR;

namespace ProjectTemplate.APPLICATION.Dtos.Queries.SomeQueries.Something
{
    public class SomethingQueryValidator : AbstractValidator<SomethingQuery>
    {
        public SomethingQueryValidator()
        {
            RuleFor(x => x.SomeId)                
                .Must(x => x > 0);
            
        }
    }
}
