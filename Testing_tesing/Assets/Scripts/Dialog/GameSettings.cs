namespace Dialog
{
    // In-memory form of settings.xml (see Schemas/settings.xsd).
    public class GameSettings
    {
        public string OllamaUrl;
        public string KeepAlive;
        public ModelRole Actor;
        public ModelRole Evaluator;
    }

    public class ModelRole
    {
        public string Model;
        public float Temperature;
        public int ContextLength;
        public int MaxTokens;
    }
}
