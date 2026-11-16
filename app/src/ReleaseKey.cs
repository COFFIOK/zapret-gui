// Zapret (C#) — открытый ключ автора для проверки подписи обновлений.
// Этот файл заполняет sign-keygen.cmd (один раз). Секретная половина ключа хранится только у автора
// и в программу не попадает. Пока ключ пустой, обновления проверяются только по контрольной сумме GitHub.
namespace ZapretCS
{
    static class ReleaseKey
    {
        public const string Modulus = "";
        public const string Exponent = "AQAB";
    }
}
