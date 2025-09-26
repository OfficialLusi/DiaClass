namespace DiaClass;

public static class Loader
{
    public static bool CheckPath(string solutionFolderPath)
    {
        if (solutionFolderPath == null || solutionFolderPath == string.Empty)
            return false;
        return true;
    }


}
