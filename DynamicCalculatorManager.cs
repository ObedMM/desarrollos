using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text;
using DynamicCode;
using Microsoft.CSharp;
using Raiz.Scoring.Persistencia;
using Scoring.BLL;

namespace Raiz.Scoring.BLL
{
    public class DynamicCalculatorManager
    {
       private readonly VariableBLL _objVar=new VariableBLL();
       private readonly FormulaBLL _objFormula = new FormulaBLL();
       private static MethodInfo method;
       private static object instance; 

        public List<string> CompileFormula(int codModelo,string formula)
        {
            var variables= _objVar.ListarVariables(true, 0, codModelo);
            var sbProperties = new StringBuilder();
            var sbFormula = new StringBuilder();
            sbFormula.AppendLine("public double Calculate(){");
            sbFormula.AppendFormat("double result=0; result={0}; return result;", formula);
            sbFormula.AppendLine("}");

            foreach (var variable in variables)
            {
                sbProperties.AppendFormat("public double {0}{{get;set;}}", variable.nombreVariable);
                //formula.Replace( variable.nombreVariable, "(10)");
            }
            string code = string.Format(@"using System; public class variables{{ {0} {1} }}", sbProperties.ToString(), sbFormula.ToString());
            CompilerResults compilerResults = CompileScript(code);
            
            var resultado=new List<string>();

            if (compilerResults.Errors.HasErrors)
            {
                var result= compilerResults.Errors;
                foreach (var error in result)
                {
                    resultado.Add(((CompilerError) error).ErrorText);

                }
            }
            return resultado;
        }

        public void TestModelo(int codModelo,string parms,ref decimal ratio,ref string perfil,ref string color)
        {
            var assembly = BuildAssembly(codModelo);
            
            instance = Activator.CreateInstance(assembly.GetType("variables"));
            method = assembly.GetType("LeerData").GetMethod("LeerVariables");
            Type tipo = assembly.GetType("variables");

            var parametros = new Dictionary<string, object>();
            parametros.Add("codModelo", codModelo);
            parametros.Add("parms", parms);
            CallDL(assembly.GetType("variables"), parametros, "usp_scoring_dynamicCalculator");

            var result = ExecMethod(assembly, "variables", "Calculate");
            ratio = Convert.ToDecimal(result);
            GetPerfilPorRatio(codModelo,ratio ,ref perfil,ref color);
        }

        public void TestModeloBD(int codModelo, string parms, string variables, ref decimal ratio, ref string perfil,ref string descPerfil, ref string color)
        {
            var result = TestResultadoEvaluacion(codModelo, parms,variables);
            if (result != null)
            {
                ratio = result.Ratio;
                perfil = result.Perfil;
                descPerfil = result.DescPerfil;
                color = result.Color;
            }
        }


        public void RunModelo(string numSolicitud,string dni,ref decimal ratio, ref string perfil,ref string color)
        {
            int codModelo = GetModeloPorSolicitud(numSolicitud, dni);
            var assembly = BuildAssembly(codModelo);
            
            instance = Activator.CreateInstance(assembly.GetType("variables"));
            method = assembly.GetType("LeerData").GetMethod("LeerVariables");
            Type tipo = assembly.GetType("variables");

            var parametros = new Dictionary<string, object>();
            parametros.Add("codModelo", codModelo);
            parametros.Add("dni", dni);
            parametros.Add("numSol",numSolicitud);
            CallDL(tipo, parametros, "usp_scoring_procesar_modelo");

            var result = ExecMethod(assembly, "variables", "Calculate");
            ratio = Convert.ToDecimal(result);
            GetPerfilPorRatio(codModelo, ratio, ref perfil, ref color);
        }

        private Assembly BuildAssembly(int codModelo)
        {
            var variables = _objVar.ListarVariables(true, 0, codModelo);
            var formula = _objFormula.ListarFormula(true, codModelo).FirstOrDefault();
            var sbProperties = new StringBuilder();
            var sbParam = new StringBuilder();
            var sbFormula = new StringBuilder();

            sbParam.AppendLine("public static variables LeerVariables(IDataReader rdr){");
            sbParam.AppendLine("var result=new variables();");
            sbFormula.AppendLine("public double Calculate(){");
            sbFormula.AppendFormat("double result=0; result={0}; return result;", formula.formula);
            sbFormula.AppendLine("}");

            foreach (var variable in variables)
            {
                sbProperties.AppendFormat("public double {0}{{get;set;}}", variable.nombreVariable);
                sbParam.AppendFormat("result.{0}=(double)rdr[\"{0}\"];", variable.nombreVariable);
            }
            sbParam.AppendLine("return result;}");
            string code = string.Format(@"public class variables{{ {0} {1} }}", sbProperties.ToString(), sbFormula.ToString());
            string code2 = string.Format(@"using System;using System.Data;using System.Globalization;
                                            public class LeerData{{ {0} }}", sbParam.ToString());
            CompilerResults compilerResults = CompileScript(code2 + code);

            if (compilerResults.Errors.HasErrors)
            {
                throw new InvalidOperationException("Expression has a syntax error.");
            }

            Assembly assembly = compilerResults.CompiledAssembly;
            return assembly;
        }

        private object ExecMethod(Assembly assembly,string clase,string metodo)
        {
            MethodInfo calcMethod = assembly.GetType(clase).GetMethod(metodo);
            var result = calcMethod.Invoke(instance, new object[] { });
            return result;
        }

        public static CompilerResults CompileScript(string source)
        {
            var parms = new CompilerParameters();
            parms.GenerateExecutable = false;
            parms.GenerateInMemory = true;
            parms.IncludeDebugInformation = false;
            parms.ReferencedAssemblies.Add("System.dll");
            parms.ReferencedAssemblies.Add("System.Data.dll");
            parms.ReferencedAssemblies.Add("System.Globalization.dll");
            CodeDomProvider compiler = CSharpCodeProvider.CreateProvider("CSharp");
            return compiler.CompileAssemblyFromSource(parms, source);
        }

        static void CallDL(Type t, Dictionary<string, object> parms,string sp)
        {
            dynamic tipo = Array.CreateInstance(t, 0);
            instance = CallDLProxy(tipo,parms,sp);

            
        }

        static T CallDLProxy<T>(T[] array,Dictionary<string,object> parms,string sp)
        {
            var objBL = new DataDL<T>("LFS");
            var resultado = objBL.Listar(sp, parms, method).FirstOrDefault();
            return resultado;
        }

        static void GetPerfilPorRatio(int codModelo,decimal ratio,ref string perfil,ref string color)
        {
            using (var ctx = new ScoringContext())
            {
                var result = (from tm in ctx.TBMScoringRango
                              join td in ctx.TBMScoringPerfil on tm.perfilRiesgo equals td.codPerfil
                              where tm.limiteInferior <= ratio && tm.limiteSuperior >= ratio 
                              && tm.estado==true 
                              && td.estado==true
                              && tm.codModelo==codModelo
                              select new { td.nombrePerfil,td.color }).FirstOrDefault();
                if (result != null)
                {
                    perfil = result.nombrePerfil;
                    color = result.color;
                }
            }
        }

        static int GetModeloPorSolicitud(string numSolicitud,string numDni)
        {
            var objBL = new DataDL<int>("LFS");
            var parametros = new Dictionary<string, object>();
            parametros.Add("dni", numDni);
            parametros.Add("numSol", numSolicitud);
            var resultado = objBL.Listar("usp_scoring_evaluar_modelo", parametros, LeerModelo).FirstOrDefault();
            return resultado;

        }

        static DynamicResult TestResultadoEvaluacion(int codModelo,string parms,string variables)
        {
            var objBL = new DataDL<DynamicResult>("LFS");
            var parametros = new Dictionary<string, object>();
            parametros.Add("codModelo", codModelo);
            parametros.Add("parms", parms);
            parametros.Add("variables",variables);
            var resultado = objBL.Listar("usp_scoring_dynamicCalculator", parametros, LeerResultado).FirstOrDefault();
            return resultado;
            
        }

        public static int LeerModelo(IDataReader rdr)
        {
            var result = rdr["codModelo"];
            return (int) rdr["codModelo"];
        }

        static DynamicResult LeerResultado(IDataReader rdr)
        {
            var result = new DynamicResult();
            result.Ratio = Convert.ToDecimal(rdr["Ratio"]);
            result.Perfil = (string) rdr["nombrePerfil"];
            result.DescPerfil = (string) rdr["descPerfil"];
            result.Color = (string) rdr["color"];
            return result;
        }


    }

    public class DynamicResult
    {
        public decimal Ratio { get; set; }
        public string Perfil { get; set; }
        public string DescPerfil { get; set; }
        public string Color { get; set; }
    }

}
