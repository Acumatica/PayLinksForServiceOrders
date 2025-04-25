using PX.Data;
using PX.Objects.CA;
using PX.Objects.CC;
using PX.Objects.CS;
using PX.Objects.FS;

namespace PaymentLinksForServiceOrder
{
    public class ServiceOrderPayLink : PXCacheExtension<FSServiceOrder>
	{
        public static bool IsActive()
        {
            return PXAccess.FeatureInstalled<FeaturesSet.acumaticaPayments>();
        }

        #region PayLinkID
        public abstract class usrPayLinkID : PX.Data.BQL.BqlInt.Field<usrPayLinkID> { }
		/// <summary>
		/// Acumatica specific Payment Link Id.
		/// </summary>
		[PXDBInt]
		public int? UsrPayLinkID { get; set; }
		#endregion

		#region ProcessingCenterID
		public abstract class usrProcessingCenterID : PX.Data.BQL.BqlString.Field<usrProcessingCenterID> { }
		
		[PXDBString(10, IsUnicode = true)]
		[PXSelector(typeof(Search2<CCProcessingCenter.processingCenterID,
			InnerJoin<CashAccount, On<CCProcessingCenter.cashAccountID, Equal<CashAccount.cashAccountID>>,
			InnerJoin<CCProcessingCenterBranch, On<CCProcessingCenterBranch.processingCenterID, Equal<CCProcessingCenter.processingCenterID>>>>,
			Where<CashAccount.curyID, Equal<Current<FSServiceOrder.curyID>>,
				And<CCProcessingCenter.allowPayLink, Equal<True>,
				And<CCProcessingCenter.isActive, Equal<True>,
				And<CCProcessingCenterBranch.branchID, Equal<Current<FSServiceOrder.branchID>>>>>>>),
			DescriptionField = typeof(CCProcessingCenter.name))]
		[PXUIField(DisplayName = "Processing Center", Visible = true, Enabled = true)]
		public string UsrProcessingCenterID { get; set; }
		#endregion

		#region DeliveryMethod
		public abstract class usrDeliveryMethod : PX.Data.BQL.BqlString.Field<usrDeliveryMethod> { }
		/// <summary>
		/// Payment Link delivery method (N - none, E - email).
		/// </summary>
		[PXDBString(1, IsFixed = true)]
		[PayLinkDeliveryMethod.List]
		[PXUIField(DisplayName = "Link Delivery Method")]
		public string UsrDeliveryMethod { get; set; }
		#endregion
	}
}