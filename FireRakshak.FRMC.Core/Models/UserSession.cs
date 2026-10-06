using System;
using FireRakshak.FRMC.Core.Enums;

namespace FireRakshak.FRMC.Core.Models
{
    public class UserSession
    {
        public string SessionId { get; }
        public string Username { get; }
        public UserRole Role { get; }
        public DateTime LoginTime { get; }

        public UserSession(string sessionId, string username, UserRole role, DateTime loginTime)
        {
            SessionId = sessionId;
            Username = username;
            Role = role;
            LoginTime = loginTime;
        }
    }
}
