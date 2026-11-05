using Advocacia.Bot.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Advocacia.Bot.Infrastructure.Configuration;

public sealed class ChannelRegistry(IOptionsMonitor<WhatsAppOptions> options) : IChannelRegistry
{
    public WhatsAppChannel? FindByPhoneNumberId(string phoneNumberId) =>
        options.CurrentValue.Channels.FirstOrDefault(channel => channel.PhoneNumberId == phoneNumberId);

    public WhatsAppChannel? FindByTenantId(Guid tenantId) =>
        options.CurrentValue.Channels.FirstOrDefault(channel => channel.TenantId == tenantId);
}
