namespace PetGPT.Personas;

public sealed class PersonaProfile
{
    internal PersonaProfile(
        string sourcePath,
        string voicePath,
        string characterId,
        string identity,
        string worldview,
        IEnumerable<string> values,
        IEnumerable<string> likes,
        IEnumerable<string> dislikes,
        IEnumerable<string> fears,
        IEnumerable<string> taboos,
        IEnumerable<string> humor,
        string relationshipToUser,
        IEnumerable<string> appraisalPrinciples,
        string factualAnswerStyle,
        string voice)
    {
        SourcePath = sourcePath;
        VoicePath = voicePath;
        CharacterId = characterId;
        Identity = identity;
        Worldview = worldview;
        Values = Array.AsReadOnly(values.ToArray());
        Likes = Array.AsReadOnly(likes.ToArray());
        Dislikes = Array.AsReadOnly(dislikes.ToArray());
        Fears = Array.AsReadOnly(fears.ToArray());
        Taboos = Array.AsReadOnly(taboos.ToArray());
        Humor = Array.AsReadOnly(humor.ToArray());
        RelationshipToUser = relationshipToUser;
        AppraisalPrinciples = Array.AsReadOnly(appraisalPrinciples.ToArray());
        FactualAnswerStyle = factualAnswerStyle;
        Voice = voice;
    }

    public string SourcePath { get; }
    public string VoicePath { get; }
    public string CharacterId { get; }
    public string Identity { get; }
    public string Worldview { get; }
    public IReadOnlyList<string> Values { get; }
    public IReadOnlyList<string> Likes { get; }
    public IReadOnlyList<string> Dislikes { get; }
    public IReadOnlyList<string> Fears { get; }
    public IReadOnlyList<string> Taboos { get; }
    public IReadOnlyList<string> Humor { get; }
    public string RelationshipToUser { get; }
    public IReadOnlyList<string> AppraisalPrinciples { get; }
    public string FactualAnswerStyle { get; }
    public string Voice { get; }
}
