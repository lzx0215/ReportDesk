using System;
using System.Text;
namespace ReportDesk.Core;

// Import-copy syntax repair only. Quoted text, identifiers and comments are opaque.
public static class SqlPunctuation
{
    public static string Normalize(string sql, out int count)
    {
        count=0; var output=new StringBuilder(sql); char quote='\0'; bool line=false,block=false;
        for(var i=0;i<sql.Length;i++)
        {
            var c=sql[i]; var next=i+1<sql.Length?sql[i+1]:'\0';
            if(line) { if(c=='\n')line=false; continue; }
            if(block) { if(c=='*'&&next=='/') {block=false;i++;} continue; }
            if(quote!='\0') { if(c==quote) {if(next==quote)i++;else quote='\0';} continue; }
            if(c=='-'&&next=='-'){line=true;i++;continue;}
            if(c=='/'&&next=='*'){block=true;i++;continue;}
            if(c=='\''||c=='"'){quote=c;continue;}
            if(c=='（'||c=='）') {output[i]=c=='（'?'(':')';count++;}
        }
        return output.ToString();
    }
}
