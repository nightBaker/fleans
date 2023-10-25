namespace Fleans.Domain;

public interface IActivity
{
    IActivity? Next { get; }
    
    void Execute(IContext context);
    void Rollback();
}