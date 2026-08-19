using System.Globalization;
using System.Speech.Recognition;

namespace AtcListener;

// Fase 1 - gramatica estricta: "«callsign», «frase de intencion»".
// El reconocedor no acepta lenguaje libre - solo estas combinaciones exactas.
public static class AtcGrammar
{
    private static readonly (string Phrase, AtcIntent Intent)[] IntentPhrases =
    [
        ("solicito rodaje", AtcIntent.SolicitarRodaje),
        ("listo para despegue", AtcIntent.ListoParaDespegue),
        ("en posicion listo", AtcIntent.ListoParaDespegue),
        ("solicito aproximacion", AtcIntent.SolicitarAproximacion),
        ("en final", AtcIntent.ReporteFinal),
        ("pista despejada", AtcIntent.PistaDespejada)
    ];

    public static Grammar Build(CultureInfo culture, string[] callsigns)
    {
        var callsignChoiceBuilder = new GrammarBuilder { Culture = culture };
        callsignChoiceBuilder.Append(new Choices(callsigns));

        var alternatives = IntentPhrases.Select(entry =>
        {
            var intentPart = new GrammarBuilder { Culture = culture };
            intentPart.Append(new SemanticResultValue(entry.Phrase, entry.Intent.ToString()));

            var full = new GrammarBuilder { Culture = culture };
            full.Append(new SemanticResultKey("callsign", callsignChoiceBuilder));
            full.Append(new SemanticResultKey("intent", intentPart));
            return full;
        }).ToArray();

        var top = new GrammarBuilder(new Choices(alternatives)) { Culture = culture };
        return new Grammar(top) { Name = "AtcPhraseologyFase1" };
    }
}
