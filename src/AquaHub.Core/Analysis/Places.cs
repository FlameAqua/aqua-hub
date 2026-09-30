namespace AquaHub.Core.Analysis;

/// <summary>
/// A small, curated gazetteer of place tokens (countries, adjectives, regions, US states, major cities) in the same
/// folded, stemmed form as <see cref="TextTools.Signature"/>. Clustering uses it so that two headlines which share
/// only a place ("China", "Dublin"), or share words while naming different places, are not merged into one story.
/// Curated rather than generated from country names, which would turn words like "man" (Isle of Man) or "city"
/// (Vatican City) into places.
/// </summary>
public static class Places
{
    private const string Names =
        // countries (single distinctive word)
        "afghanistan albania algeria andorra angola argentina armenia australia austria azerbaijan bahamas bahrain " +
        "bangladesh barbados belarus belgium belize benin bhutan bolivia bosnia botswana brazil brunei bulgaria burkina " +
        "burundi cambodia cameroon canada chad chile china colombia comoros congo croatia cuba cyprus czechia denmark " +
        "djibouti dominica ecuador egypt eritrea estonia eswatini ethiopia fiji finland france gabon gambia georgia " +
        "germany ghana greece grenada guatemala guinea guyana haiti honduras hungary iceland india indonesia iran iraq " +
        "ireland israel italy jamaica japan kazakhstan kenya kiribati kosovo kuwait kyrgyzstan laos latvia lebanon " +
        "lesotho liberia libya liechtenstein lithuania luxembourg madagascar malawi malaysia maldives mali malta " +
        "mauritania mauritius mexico micronesia moldova monaco mongolia montenegro morocco mozambique myanmar namibia " +
        "nauru nepal netherlands nicaragua niger nigeria norway oman pakistan palau palestine panama paraguay peru " +
        "philippines poland portugal qatar romania russia rwanda samoa senegal serbia seychelles singapore slovakia " +
        "slovenia somalia spain sudan suriname sweden switzerland syria taiwan tajikistan tanzania thailand togo tonga " +
        "tunisia turkey turkmenistan tuvalu uganda ukraine uruguay uzbekistan vanuatu venezuela vietnam yemen zambia " +
        "zimbabwe korea arabia zealand lanka salvador timor britain america " +
        // adjectives / demonyms
        "american british irish chinese russian ukrainian israeli iranian french german italian spanish indian japanese " +
        "korean brazilian mexican canadian australian turkish syrian lebanese palestinian egyptian saudi european african " +
        "asian scottish welsh dutch polish greek swiss swedish norwegian danish finnish belgian austrian portuguese " +
        "argentine venezuelan colombian chilean peruvian cuban pakistani afghan iraqi yemeni qatari emirati nigerian " +
        "kenyan ethiopian sudanese somali congolese thai vietnamese indonesian filipino malaysian taiwanese serbian " +
        "croatian hungarian romanian bulgarian slovak belarusian armenian azerbaijani kazakh " +
        // regions
        "africa europe asia gaza crimea donbas kashmir tibet catalonia scotland wales england balkans scandinavia " +
        "siberia " +
        // US states (distinctive words)
        "alabama alaska arizona arkansas california colorado connecticut delaware florida hawaii idaho illinois indiana " +
        "iowa kansas kentucky louisiana maine maryland massachusetts michigan minnesota mississippi missouri montana " +
        "nebraska nevada hampshire ohio oklahoma oregon pennsylvania tennessee texas utah vermont virginia washington " +
        "wisconsin wyoming " +
        // major cities
        "london paris berlin madrid rome lisbon brussels amsterdam vienna prague warsaw budapest athens stockholm oslo " +
        "copenhagen helsinki dublin belfast cork galway limerick edinburgh glasgow manchester birmingham liverpool moscow " +
        "kyiv kiev kharkiv odesa minsk istanbul ankara tehran baghdad damascus beirut jerusalem aviv cairo riyadh doha " +
        "dubai kabul islamabad delhi mumbai beijing shanghai tokyo seoul pyongyang taipei bangkok jakarta manila sydney " +
        "melbourne auckland toronto montreal vancouver ottawa chicago boston atlanta miami houston dallas seattle denver " +
        "phoenix detroit angeles francisco oakland pleasanton alameda philadelphia baltimore bogota caracas lima santiago " +
        "aires paulo johannesburg durban pretoria lagos nairobi ababa khartoum kinshasa";

    private static readonly HashSet<string> Tokens = new(Names.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(TextTools.Stem), StringComparer.Ordinal);

    /// <summary>Adds the user's own local words (e.g. Irish counties) so they count as places too.</summary>
    public static HashSet<string> With(IEnumerable<string> extra)
    {
        var set = new HashSet<string>(Tokens, StringComparer.Ordinal);
        foreach (var e in extra)
            foreach (var t in TextTools.Signature(e)) set.Add(t);
        return set;
    }

    public static bool IsPlace(string signatureToken) => Tokens.Contains(signatureToken);
}
