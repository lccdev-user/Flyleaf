namespace FlyleafLib.Custom;

public interface ICustomRenderer
{
    event Action CustomProcessRequests;
    event Action CustomSetSize;    
    double InitialZoom { get; }
    double MaximalZoom { get; }
    double ValidateZoom(double zoom);

    void CheckControlSize(int width, int height);
}
