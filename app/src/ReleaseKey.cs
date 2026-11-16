// Zapret (C#) — открытый ключ автора для проверки подписи обновлений.
// Этот файл заполняет sign-keygen.cmd (один раз). Секретная половина ключа хранится только у автора
// и в программу не попадает. Пока ключ пустой, обновления проверяются только по контрольной сумме GitHub.
namespace ZapretCS
{
    static class ReleaseKey
    {
        public const string Modulus = "wJixEdrGeKVBJh8im7+eKSVez+eeHzxaUmoHqOCYk2MihweM4Xyqj8sCv7RUj9JZaHYPjqMIt+31gsZOEljLEAIzsPF9lgd5Fcvz0/RUGT4Krzbhhdq6HsikqWBYQbFDRhpHCdAOlOobc7JnGxFdotj/mPUCyRSS4eZWqX1GWULsdam3uu0Ic1E9adXMUNd7nstP6bDr6PjbKGjyCJMBWtGI+6DqFQQG4PnWkk0KUXx3t9B+1+EKbNrm8lUHi9x+oDvxDTLtIzwUI3wIl0qbYxmIIDEOr6LPIynjf/i4kSIrhbR6KWhQGoWgJBh3IzSoj3yp9bKIIQGA4F1fHteaX/0+bwDTMqm5Jk0a67xF8555WYQHXNjKhTljgfoixtXAImogPrlFfCS5G/KLmH61Q+L9PWPLVmY/vlCQlPHMZ8FfDtKqNmxL/Pr/O9S6YjQmtw2kRfIG+IQWUOwBSGXVutrMx+R92Xebake3cZ0/N3hQuGa3hzojvpSJl7V97PuN";
        public const string Exponent = "AQAB";
    }
}