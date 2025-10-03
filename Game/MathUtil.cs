
public static class MathUtil
{
    public static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }
}
