using System.Text;

namespace AtcListener;

// Windows Speech Recognition no expande automaticamente cifras a su forma hablada
// (probado: "Viper 1-1" en la gramatica NO reconoce "viper uno uno" dicho en voz alta).
// Por convencion real de radio, los numeros de callsign se dicen cifra a cifra
// ("uno uno", no "once"), asi que convertimos cada digito por separado.
public static class CallsignNormalizer
{
    private static readonly string[] DigitWords =
        ["cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve"];

    public static string ForGrammar(string raw)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        void FlushCurrent()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (var ch in raw)
        {
            if (char.IsDigit(ch))
            {
                FlushCurrent();
                tokens.Add(DigitWords[ch - '0']);
            }
            else if (char.IsLetter(ch))
            {
                current.Append(ch);
            }
            else
            {
                // separadores (espacio, guion, guion bajo, punto...) simplemente cortan token
                FlushCurrent();
            }
        }

        FlushCurrent();

        return string.Join(' ', tokens).ToLowerInvariant();
    }
}
