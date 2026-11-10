using OpenPayments.Psp;
using OpenPayments.Psp.Resilience;
using OpenPayments.Reconciler;
using OpenPayments.Transitions;

var builder = Host.CreateApplicationBuilder(args);
var database = builder.Configuration["DATABASE_URL"]
    ?? "postgres://op_app:op_local_only@127.0.0.1:25432/openpayments";
#pragma warning disable CA2000 // The host owns the singleton until shutdown.
builder.Services.AddSingleton(new TxRunner(database));
#pragma warning restore CA2000
builder.Services.AddSingleton(services =>
{
    var store = new PaymentStore(services.GetRequiredService<TxRunner>());
    var adapters = services.GetRequiredService<IPspAdapterFactory>();
    store.AuthorizationWindowFor = psp => adapters.Capabilities(psp).AuthorizationWindow;
    return store;
});
builder.Services.AddSingleton<RecoveryStore>();
builder.Services.AddSingleton<AccountGate>();
builder.Services.AddHttpClient("psp", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["PSP_URL"] ?? "http://127.0.0.1:9080");
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<IPspAdapterFactory>(services =>
    new MockPspAdapterFactory(services.GetRequiredService<IHttpClientFactory>().CreateClient("psp")));
builder.Services.AddSingleton(ReconcilerOptions.FromConfiguration(builder.Configuration));
builder.Services.AddHostedService<TimerWorker>();
builder.Services.AddHostedService<EscalationWorker>();
builder.Services.AddHostedService<ParkedEventWorker>();
builder.Services.AddHostedService<RecoveryWorker>();
builder.Build().Run();
