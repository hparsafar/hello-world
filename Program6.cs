////////test my wallet in main net first to get balance and then test to send transaction
using CardanoSharp.Wallet.CIPs.CIP2;
using CardanoSharp.Wallet.CIPs.CIP2.Models;
using CardanoSharp.Wallet.Models.Transactions;
using CardanoSharp.Wallet.Extensions;
using CardanoSharp.Wallet.Extensions.Models.Transactions;
using CardanoSharpAsset = CardanoSharp.Wallet.Models.Asset;
using CardanoSharp.Koios.Client;
using Refit;
using CardanoSharp.Wallet.Extensions.Models;
using CardanoSharp.Wallet.TransactionBuilding;
using CardanoSharp.Wallet.Models.Addresses;
using CardanoSharp.Wallet.Models;
using CardanoSharp.Wallet.Enums;
using CardanoSharp.Wallet;
using CardanoSharp.Wallet.Models.Derivations;
using CardanoSharp.Wallet.Models.Keys;
using CardanoSharp.Wallet.Models.Transactions.Scripts;
using CardanoSharp.Wallet.Utilities;
using System.Net;



var networkClient = RestService.For<INetworkClient>("https://api.koios.rest/api/v1");
var epochClient = RestService.For<IEpochClient>("https://api.koios.rest/api/v1");
var blockClient = RestService.For<IBlockClient>("https://api.koios.rest/api/v1");
var transactionClient = RestService.For<ITransactionClient>("https://api.koios.rest/api/v1");
var addressClient = RestService.For<IAddressClient>("https://api.koios.rest/api/v1");
var accountClient = RestService.For<IAccountClient>("https://api.koios.rest/api/v1");
var assetClient = RestService.For<IAssetClient>("https://api.koios.rest/api/v1");
var poolClient = RestService.For<IPoolClient>("https://api.koios.rest/api/v1");
var scriptClient = RestService.For<IScriptClient>("https://api.koios.rest/api/v1");



Mnemonic myWalletMnemonic = new MnemonicService().Restore("my mnemonics");
//Derive Payment Public Key

IIndexNodeDerivation myWalletNode = myWalletMnemonic.GetMasterNode()
    .Derive(PurposeType.Shelley)
    .Derive(CoinType.Ada)
    .Derive(0)
    .Derive(RoleType.ExternalChain)
    .Derive(0);

myWalletNode.SetPublicKey();
//Derive Stake Public Key
var myWalletstakeNode = myWalletMnemonic.GetMasterNode()
    .Derive(PurposeType.Shelley)
    .Derive(CoinType.Ada)
    .Derive(0)
    .Derive(RoleType.Staking)
    .Derive(0);
myWalletstakeNode.SetPublicKey();

var mychangeAddress = myWalletMnemonic.GetMasterNode()
    .Derive(PurposeType.Shelley)
    .Derive(CoinType.Ada)
    .Derive(0)
    .Derive(RoleType.InternalChain)
    .Derive(0);
mychangeAddress.SetPublicKey();

IAddressService addressService = new AddressService();

var senderAddress = addressService.GetBaseAddress(
myWalletNode.PublicKey,
myWalletstakeNode.PublicKey,
NetworkType.Mainnet).ToString();
var destination = "addr1qypf6mmegd0egmey2jfcwvsxl7xc7g6pr9ldvsxp3zu6xfnesh43glgahr2y53eaju56eyzknrhy8n7qvnwc84tz9pqq05drj5";

var changeAddress = addressService.GetBaseAddress(
mychangeAddress.PublicKey,
myWalletstakeNode.PublicKey,
NetworkType.Mainnet).ToString();

//1. Get UTxOs
var utxos = await GetUtxos(senderAddress);

///2. Create the Body
var transactionBody = TransactionBodyBuilder.Create;

//set outputs
transactionBody.AddOutput(destination.ToBytes(), 25000000);
transactionBody.AddOutput(changeAddress.ToBytes(), 75000000);



//perform coin selection
var coinSelection = ((TransactionBodyBuilder)transactionBody).UseLargestFirstWithImprove(utxos);

//add the inputs from coin selection to transaction body builder
AddInputsFromCoinSelection(coinSelection, transactionBody);


//get protocol parameters and set default fee
var epochResponse = await epochClient.GetEpochInformation();
var ppResponse = await epochClient.GetProtocolParameters();
var protocolParameters = ppResponse.Content.FirstOrDefault();


//get network tip and set ttl
var blockSummaries = (await networkClient.GetChainTip()).Content;
var ttl = 2500 + (uint)blockSummaries.First().AbsSlot;
transactionBody.SetTtl(ttl);

///3. Add Witnesses
var witnessSet = TransactionWitnessSetBuilder.Create;
witnessSet.AddVKeyWitness(myWalletNode.PublicKey, myWalletNode.PrivateKey);
witnessSet.AddVKeyWitness(myWalletstakeNode.PublicKey, myWalletstakeNode.PrivateKey);



var transaction = TransactionBuilder.Create;
transaction.SetBody(transactionBody);
transaction.SetWitnesses(witnessSet);

//get a draft transaction to calculate fee
var draft = transaction.Build();
var fee = draft.CalculateFee(protocolParameters.MinFeeA, protocolParameters.MinFeeB);

//update fee and change output
transactionBody.SetFee(fee);
var raw = transaction.Build();
raw.TransactionBody.TransactionOutputs.Last().Value.Coin -= fee;





var signed = raw.Serialize();

try
{
    using MemoryStream stream = new MemoryStream(signed);
    try
    {
        Console.WriteLine("Sending...");
        var result = await transactionClient.Submit(stream);
        Console.WriteLine($"Tx ID: {result.Content}");
        //MemoryStream stream2 = new MemoryStream(signedTx);
        //var result2 = await transactionClient.Submit(stream2);
        //Console.WriteLine($"Tx ID: {result2.Content}");
        //var txId = await transactionClient.Submit(stream);
        //var xxx = 1;
    }
    catch (Exception e)
    {
        Console.Write(e.Message);
    }
}
catch (Exception e)
{
    Console.WriteLine(e.Message);
}




async Task<List<Utxo>> GetUtxos(string address)
{
    try
    {
        var addressBulkRequest = new AddressBulkRequest { Addresses = new List<string> { address } };
        var addressResponse = (await addressClient.GetAddressInformation(addressBulkRequest));
        var addressInfo = addressResponse.Content;
        var utxos = new List<Utxo>();

        foreach (var ai in addressInfo.SelectMany(x => x.UtxoSets))
        {
            if (ai is null) continue;
            var utxo = new Utxo()
            {
                TxIndex = ai.TxIndex,
                TxHash = ai.TxHash,
                Balance = new Balance()
                {
                    Lovelaces = ulong.Parse(ai.Value)
                }
            };

            var assetList = new List<CardanoSharpAsset>();
            foreach (var aa in ai.AssetList)
            {
                assetList.Add(new CardanoSharpAsset()
                {
                    Name = aa.AssetName,
                    PolicyId = aa.PolicyId,
                    Quantity = ulong.Parse(aa.Quantity)
                });
            }

            utxo.Balance.Assets = assetList;
            utxos.Add(utxo);
        }

        return utxos;
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.Message);
        return null;
    }
}

void AddInputsFromCoinSelection(CoinSelection coinSelection, ITransactionBodyBuilder transactionBody)
{
    foreach (var i in coinSelection.Inputs)
    {
        transactionBody.AddInput(i.TransactionId, i.TransactionIndex);
    }
}

void AddChangeOutputs(ITransactionBodyBuilder ttb, List<TransactionOutput> outputs, string address)
{
    foreach (var output in outputs)
    {
        ITokenBundleBuilder? assetList = null;

        if (output.Value.MultiAsset is not null)
        {
            assetList = TokenBundleBuilder.Create;
            foreach (var ma in output.Value.MultiAsset)
            {
                foreach (var na in ma.Value.Token)
                {
                    assetList.AddToken(ma.Key, na.Key, na.Value);
                }
            }
        }

        ttb.AddOutput(new Address(address), output.Value.Coin, assetList);
    }
}