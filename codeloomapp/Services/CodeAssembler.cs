using System.Text;
using codeloomapp.Models;

namespace codeloomapp.Services;

public static class CodeAssembler
{
    public static string Assemble(CodeFile file)
    {
        var builder = new StringBuilder();

        foreach (var usingStatement in file.UsingStatements)
            builder.AppendLine(usingStatement);

        if (file.UsingStatements.Count > 0)
            builder.AppendLine();

        builder.Append("public class ")
               .Append(file.ClassName)
               .Append(" : ")
               .AppendLine(file.BaseClass);
        builder.AppendLine("{");

        for (var index = 0; index < file.Subfiles.Count; index++)
        {
            var subfile = file.Subfiles[index];
            builder.Append("    // ===== ")
                   .Append(subfile.Name.ToUpperInvariant())
                   .AppendLine(" =====");
            builder.AppendLine();

            foreach (var line in subfile.Code.Replace("\r\n", "\n").Split('\n'))
                builder.Append("    ").AppendLine(line);

            if (index < file.Subfiles.Count - 1)
                builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }
}
