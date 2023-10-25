namespace Fleans.Domain;

public interface IActivity
{       
    Task ExecuteAsync(IContext context);    
}