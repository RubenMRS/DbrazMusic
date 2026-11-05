namespace MusicBot.Audio;

/// <summary>Erro ao entrar/manter a ligação ao canal de voz.</summary>
public sealed class VoiceConnectionException(string message, Exception? inner = null)
    : Exception(message, inner);
