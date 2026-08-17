namespace Tessera.Core.Channels;

// Resolves the right IChannel by name for a given message/identity — needed as soon as a
// second channel exists alongside Telegram (docs/01-architettura.md). Before this, every
// consumer captured a single injected IChannel, which was silently always Telegram; with two
// or more IChannel registrations, a single-instance injection becomes ambiguous (.NET DI just
// picks the last one registered) rather than a compile error, so this is the one place that
// ambiguity gets resolved deliberately instead of by accident.
public interface IChannelRegistry
{
    IChannel? TryGet(string channelName);
}
