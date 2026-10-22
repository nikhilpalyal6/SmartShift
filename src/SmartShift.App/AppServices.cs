using SmartShift.Core.Actions;
using SmartShift.Core.Classification;
using SmartShift.Core.Logging;
using SmartShift.Core.Queueing;
using SmartShift.Infrastructure.Logging;
using SmartShift.Infrastructure.Output;
using SmartShift.Processing;
using SmartShift.Processing.Batch;
using SmartShift.Processing.Documents;

namespace SmartShift.App;

public sealed class AppServices : IDisposable
{
    public AppServices(DiagnosticLogger logger)
    {
        Logger = logger;
        Classifier = new FileClassifier();
        Queue = new FileQueue();
        Recommendations = new RecommendationEngine(new Phase1ActionCapabilities());
        Outputs = new OutputPathAllocator();
        Processors = new ProcessorCatalog(Classifier, Outputs);
        BatchEngine = new BatchImageProcessingEngine(Processors, Outputs, Classifier);
        DocumentBuilder = new MultiImageDocumentBuilder(Outputs, Classifier);
        LogSink = new LocalFileLogSink(logger);
    }

    public DiagnosticLogger Logger { get; }
    public FileClassifier Classifier { get; }
    public FileQueue Queue { get; }
    public RecommendationEngine Recommendations { get; }
    public ProcessorCatalog Processors { get; }
    public IBatchImageProcessingEngine BatchEngine { get; }
    public IMultiImageDocumentBuilder DocumentBuilder { get; }
    public OutputPathAllocator Outputs { get; }
    public LocalFileLogSink LogSink { get; }

    public void Dispose() => LogSink.Dispose();
}
