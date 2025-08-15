using System.Reflection;
using Axon.Core.Models;

namespace Axon.Client.Services;

public class JobActivator
{
    public static JobActivator Current { get; } = new();
    
    public object? CreateInstance(JobInfo jobInfo)
    {
        var assembly = Assembly.Load(jobInfo.Assembly);

        var declaringType = assembly.GetType(jobInfo.DeclaringType);

        if (declaringType is null)
        {
            return null;
        }
        
        var instance = Activator.CreateInstance(declaringType);

        return instance;
    }
}