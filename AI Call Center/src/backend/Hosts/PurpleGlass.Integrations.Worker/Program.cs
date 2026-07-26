using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Integrations.Worker;
using PurpleGlass.Observability;
using PurpleGlass.Adapters.Telephony.Fake;
using PurpleGlass.Adapters.Telephony.Twilio;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPurpleGlassObservability(builder.Configuration, "PurpleGlass.Integrations.Worker", builder.Environment.EnvironmentName);
string connectionString = builder.Configuration.RequireConnectionString();

builder.Services.AddEventingInfrastructure(connectionString);
builder.Services.AddCallManagementInfrastructure(connectionString);
builder.Services.Configure<OutboxPublisherOptions>(
    builder.Configuration.GetSection(OutboxPublisherOptions.SectionName));
builder.Services.AddHostedService<Worker>();
builder.Services.Configure<TelephonyRuntimeOptions>(builder.Configuration.GetSection(TelephonyRuntimeOptions.SectionName));
string telephonyProvider = builder.Configuration["Telephony:Provider"] ?? "None";
bool realTelephonyEnabled = builder.Configuration.GetValue<bool>("Providers:EnableRealTelephony");
TelephonyProviderConfigurationValidator.Validate(telephonyProvider, realTelephonyEnabled);
if (realTelephonyEnabled && telephonyProvider.Equals("Twilio", StringComparison.OrdinalIgnoreCase))
{
    var twilioOptions = new TwilioTelephonyOptions
    {
        AccountSid = builder.Configuration["Telephony:Twilio:AccountSid"] ?? string.Empty,
        AuthToken = builder.Configuration["Telephony:Twilio:AuthToken"] ?? string.Empty,
        PublicBaseUrl = builder.Configuration["Telephony:PublicBaseUrl"] ?? string.Empty,
    };
    builder.Services.AddSingleton(twilioOptions);
    builder.Services.AddSingleton<ITelephonyProvider, TwilioTelephonyProvider>();
}
else if (builder.Environment.IsDevelopment() && telephonyProvider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<FakeTelephonyProvider>();
    builder.Services.AddSingleton<ITelephonyProvider>(service => service.GetRequiredService<FakeTelephonyProvider>());
}
else
{
    builder.Services.AddSingleton<ITelephonyProvider, DisabledTelephonyProvider>();
}
builder.Services.AddHostedService<TelephonyTransportWorker>();

var host = builder.Build();
host.Run();
