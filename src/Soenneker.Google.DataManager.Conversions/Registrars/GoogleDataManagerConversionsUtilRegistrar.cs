using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Google.DataManager.Conversions.Abstract;
using Soenneker.Google.DataManager.Client.Registrars;

namespace Soenneker.Google.DataManager.Conversions.Registrars;

/// <summary>
/// Uploads sales and offline conversion events through the Google Data Manager API.
/// </summary>
public static class GoogleDataManagerConversionsUtilRegistrar
{
    /// <summary>
    /// Adds <see cref="IGoogleDataManagerConversionsUtil"/> as a singleton service. <para/>
    /// </summary>
    public static IServiceCollection AddGoogleDataManagerConversionsUtilAsSingleton(this IServiceCollection services)
    {
        services.AddGoogleDataManagerClientUtilAsSingleton();
        services.TryAddSingleton<IGoogleDataManagerConversionsUtil, GoogleDataManagerConversionsUtil>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="IGoogleDataManagerConversionsUtil"/> as a scoped service. <para/>
    /// </summary>
    public static IServiceCollection AddGoogleDataManagerConversionsUtilAsScoped(this IServiceCollection services)
    {
        services.AddGoogleDataManagerClientUtilAsScoped();
        services.TryAddScoped<IGoogleDataManagerConversionsUtil, GoogleDataManagerConversionsUtil>();

        return services;
    }
}

