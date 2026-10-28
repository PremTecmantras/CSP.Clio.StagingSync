using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// Sell on Clio store
    /// </summary>
    internal class ConsolidateClioCompanies
    {
        /// <summary>
        /// We read from all companies in the database that has been connected 
        /// 
        /// We loop over the tokens and refresh them
        /// 
        /// We get all data we get and add and update, delete if deleted
        /// 
        /// How they split the sales and reports, should we create custom field with Company Name
        /// </summary>
        /// <param name="targetClioCompany"></param>
        /// <returns></returns>
        public bool Consolidate(string targetClioCompany)
        {
            bool success = false;


            return success;
        }


    }
}
