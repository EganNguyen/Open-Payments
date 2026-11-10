using OpenPayments.Reconciler;
using OpenPayments.Transitions;

var builder = Host.CreateApplicationBuilder(args);
var database = builder.Configuration["DATABASE_URL"]
    ?? "postgres://op_app:op_local_only@127.0.0.1:25432/openpayments";
#pragma warning disable CA2000 // The host owns the singleton until shutdown.
builder.Services.AddSingleton(new TxRunner(database));
#pragma warning restore CA2000
builder.Services.AddSingleton<PaymentStore>();
builder.Services.AddSingleton(ReconcilerOptions.FromConfiguration(builder.Configuration));
builder.Services.AddHostedService<ParkedEventWorker>();
builder.Build().Run();
