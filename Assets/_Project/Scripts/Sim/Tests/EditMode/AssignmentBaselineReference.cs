using System;

namespace ACunicombat.Tests.EditMode
{
    /// <summary>Whole-batch one-to-one assignment. Ambiguous alternatives remain unassigned.</summary>
    internal static class AssignmentBaselineReference
    {
        public const double Rejected = 1e12;
        private const double Unmatched = 10000;

        public static int[] Solve(double[,] costs, double separation, out double total)
            =>Solve(costs,separation,out total,out _);

        public static int[] Solve(double[,] costs, double separation, out double total,out bool[] ambiguous)
            =>Solve(costs,separation,out total,out ambiguous,out _);

        public static int[] Solve(double[,] costs, double separation, out double total,out bool[] ambiguous,out double[] margins)
        {
            var best=Optimize(costs,-1,-1,out total);
            var result=(int[])best.Clone();
            ambiguous=new bool[best.Length];
            margins=new double[best.Length];
            for(int i=0;i<margins.Length;i++)margins[i]=double.PositiveInfinity;
            for(int row=0;row<best.Length;row++)
            {
                if(best[row]<0)continue;
                var other=Optimize(costs,row,best[row],out double alternative);
                margins[row]=alternative-total;
                if(alternative-total<separation)
                    for(int i=0;i<best.Length;i++)if(other[i]!=best[i]){result[i]=-1;ambiguous[i]=true;}
            }
            return result;
        }

        private static int[] Optimize(double[,] costs,int forbiddenRow,int forbiddenColumn,out double total)
        {
            int n=costs.GetLength(0),real=costs.GetLength(1),m=real+n;
            var u=new double[n+1];var v=new double[m+1];var p=new int[m+1];var way=new int[m+1];
            for(int i=1;i<=n;i++)
            {
                p[0]=i;int j0=0;var min=new double[m+1];var used=new bool[m+1];
                for(int j=1;j<=m;j++)min[j]=Rejected;
                do
                {
                    used[j0]=true;int i0=p[j0],j1=0;double delta=Rejected;
                    for(int j=1;j<=m;j++)if(!used[j])
                    {
                        double c=j<=real?costs[i0-1,j-1]:Unmatched;
                        if(i0-1==forbiddenRow&&j-1==forbiddenColumn)c=Rejected;
                        if(!double.IsFinite(c))c=Rejected;
                        double cur=c-u[i0]-v[j];
                        if(cur<min[j]){min[j]=cur;way[j]=j0;}
                        if(min[j]<delta){delta=min[j];j1=j;}
                    }
                    for(int j=0;j<=m;j++)if(used[j]){u[p[j]]+=delta;v[j]-=delta;}else min[j]-=delta;
                    j0=j1;
                }while(p[j0]!=0);
                do{int j1=way[j0];p[j0]=p[j1];j0=j1;}while(j0!=0);
            }
            var result=new int[n];for(int i=0;i<n;i++)result[i]=-1;
            for(int j=1;j<=real;j++)if(p[j]>0&&costs[p[j]-1,j-1]<Unmatched&&!(p[j]-1==forbiddenRow&&j-1==forbiddenColumn))result[p[j]-1]=j-1;
            total=0;for(int i=0;i<n;i++)total+=result[i]<0?Unmatched:costs[i,result[i]];
            return result;
        }
    }
}
