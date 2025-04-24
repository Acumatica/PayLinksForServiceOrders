using System;

namespace PaymentLinksForServiceOrder
{
	public class ServiceOrderPayLink: PXCacheExtension<ARInvoice>
	{
public static bool IsActive()
{
	return true;
}

#region PayLinkID
public abstract class payLinkID : PX.Data.BQL.BqlInt.Field<payLinkID> { }
/// <summary>
/// Acumatica specific Payment Link Id.
/// </summary>
[PXDBInt]
public int? PayLinkID { get; set; }
#endregion

#region ProcessingCenterID
public abstract class processingCenterID : PX.Data.BQL.BqlString.Field<processingCenterID> { }
/// <exclude/>
[PXDBString(10, IsUnicode = true)]
[PXSelector(typeof(Search2<CCProcessingCenter.processingCenterID,
	InnerJoin<CashAccount, On<CCProcessingCenter.cashAccountID, Equal<CashAccount.cashAccountID>>,
	InnerJoin<CCProcessingCenterBranch, On<CCProcessingCenterBranch.processingCenterID, Equal<CCProcessingCenter.processingCenterID>>>>,
	Where<CashAccount.curyID, Equal<Current<ARInvoice.curyID>>,
		And<CCProcessingCenter.allowPayLink, Equal<True>,
		And<CCProcessingCenter.isActive, Equal<True>,
		And<CCProcessingCenterBranch.branchID, Equal<Current<ARInvoice.branchID>>>>>>>),
	DescriptionField = typeof(CCProcessingCenter.name))]
[PXUIField(DisplayName = "Processing Center", Visible = true, Enabled = true)]
public string ProcessingCenterID { get; set; }
#endregion

#region DeliveryMethod
public abstract class deliveryMethod : PX.Data.BQL.BqlString.Field<deliveryMethod> { }
/// <summary>
/// Payment Link delivery method (N - none, E - email).
/// </summary>
[PXDBString(1, IsFixed = true)]
[PayLinkDeliveryMethod.List]
[PXUIField(DisplayName = "Link Delivery Method")]
public string DeliveryMethod { get; set; }
#endregion
	}
}