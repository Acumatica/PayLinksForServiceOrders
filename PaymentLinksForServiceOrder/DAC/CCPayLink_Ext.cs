using PX.Data;
using PX.Objects.CC;
using PX.Objects.CS;

namespace PaymentLinksForServiceOrder
{
    public sealed class CCPayLink_Ext : PXCacheExtension<CCPayLink>
    {
        public static bool IsActive()
        {
            return PXAccess.FeatureInstalled<FeaturesSet.acumaticaPayments>();
        }
        #region UsrFSOrderNbr
        public abstract class usrFSOrderNbr : PX.Data.BQL.BqlString.Field<usrFSOrderNbr> { }

        [PXDBString(15, IsUnicode = true)]
        public string UsrFSOrderNbr { get; set; }
        #endregion

        #region UsrFSOrderType
        public abstract class usrFSOrderType : PX.Data.BQL.BqlString.Field<usrFSOrderType> { }

        [PXDBString(4, IsUnicode = true)]
        public string UsrFSOrderType { get; set; }
        #endregion
    }
}
