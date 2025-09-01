using System;

namespace Parse.Infrastructure.Execution
{
    /// <summary>
    /// Provides data for the event that is triggered when a message is received.
    /// </summary>
    public class MessageReceivedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the message content that was received.
        /// </summary>
        public string Message { get; }

        public MessageReceivedEventArgs(string message)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }
    }
}